"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const os = require("os");
const vscode = require("vscode");
const languageData = require("./language-data.json");

let client = null;
let richMetadata = null;
let richMetadataMap = null;

// Per-file cached args: Map<fsPath, string>
const _argsCache = new Map();

// LSP state: 'stopped' | 'starting' | 'running' | 'error'
let lspState = "stopped";
let lspOutputChannel = null;
let statusBarItem = null;
let serversProvider = null;
let outlineProvider = null;

// MCP is managed externally (via Claude Code MCP config); we show it as informational only.
// mcpExternalRunning is detected opportunistically via process listing.
let mcpOutputChannel = null;

async function activate(context) {
    registerTerminalProfile(context);

    const selector = { language: "tosh", scheme: "file" };
    lspOutputChannel = vscode.window.createOutputChannel("TōSh");
    mcpOutputChannel = vscode.window.createOutputChannel("TōSh MCP");
    context.subscriptions.push(lspOutputChannel);
    context.subscriptions.push(mcpOutputChannel);

    statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    statusBarItem.name = "TōSh";
    statusBarItem.command = "tosh.servers.focus";
    context.subscriptions.push(statusBarItem);

    // Register sidebar views
    outlineProvider = new ToshOutlineProvider(context);
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.outline", outlineProvider)
    );

    const libraryExplorerProvider = new ToshLibraryExplorerProvider(context);
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.libraryExplorer", libraryExplorerProvider)
    );

    const dependenciesProvider = new ToshDependenciesProvider(context);
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.dependencies", dependenciesProvider)
    );

    serversProvider = new ToshServersProvider();
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.servers", serversProvider)
    );

    const solutionProvider = new ToshSolutionProvider(context);
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.solution", solutionProvider)
    );
    context.subscriptions.push(solutionProvider);

    const scriptsProvider = new ToshScriptsProvider();
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.scripts", scriptsProvider)
    );
    context.subscriptions.push(scriptsProvider);

    const commandsProvider = new ToshCommandsProvider();
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("tosh.commands", commandsProvider)
    );

    // Server commands
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.lsp.start", () => startLsp(context, selector))
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.lsp.stop", () => stopLsp())
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.lsp.restart", async () => {
            await stopLsp();
            await startLsp(context, selector);
        })
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.lsp.viewLogs", () => lspOutputChannel.show())
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.mcp.viewLogs", () => mcpOutputChannel.show())
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.servers.focus", () =>
            vscode.commands.executeCommand("workbench.view.extension.tosh-explorer")
        )
    );

    // Solution / project commands
    context.subscriptions.push(vscode.commands.registerCommand("tosh.newSolution", () => solutionProvider.newSolution()));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.newProject", (node) => solutionProvider.newProject(node)));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.addFileToProject", (node) => solutionProvider.addFile(node)));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.removeFromProject", (node) => solutionProvider.removeNode(node)));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.buildProject", (node) => solutionProvider.buildProject(node)));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.buildSolution", (node) => solutionProvider.buildSolution(node)));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.solution.refresh", () => solutionProvider.refresh()));

    // Scripts commands
    context.subscriptions.push(vscode.commands.registerCommand("tosh.runScript", (node) => runScriptNode(node)));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.scripts.refresh", () => scriptsProvider.refresh()));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.outline.refresh", () => outlineProvider.refresh()));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.outline.toggleSort", () => outlineProvider.toggleSort()));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.libraryExplorer.refresh", () => libraryExplorerProvider.refresh()));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.dependencies.refresh", () => dependenciesProvider.refresh()));

    context.subscriptions.push(vscode.commands.registerCommand("tosh.runTreeSymbol", (node) => {
        if (node && node.label) {
            const editor = vscode.window.activeTextEditor;
            const program = editor ? editor.document.uri.fsPath : "";
            const terminal = vscode.window.createTerminal("TōSh Run");
            terminal.show();
            terminal.sendText(`tosh ${program} -- ${node.label}`);
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("tosh.debugTreeSymbol", (node) => {
        if (node && node.label && vscode.window.activeTextEditor) {
            vscode.debug.startDebugging(undefined, {
                type: "tosh",
                request: "launch",
                name: `Debug ${node.label}`,
                program: vscode.window.activeTextEditor.document.uri.fsPath,
                args: [String(node.label)]
            });
        }
    }));

    // Command reference
    context.subscriptions.push(vscode.commands.registerCommand("tosh.openCommandRef", () => ToshCommandRefPanel.show(context)));

    // Run / Debug commands
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.runFile", () => runActiveFile())
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.debugFile", () => debugActiveFile())
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.runSelection", () => runSelection())
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.openRepl", () => ToshReplPanel.show(context))
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.runFileWithArgs", () => runActiveFile(true))
    );
    context.subscriptions.push(
        vscode.commands.registerCommand("tosh.checkInstall", () => checkToshInstall())
    );

    // DAP descriptor factory
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory("tosh", new ToshDebugAdapterDescriptorFactory())
    );

    // Folding. Registered unconditionally: Tosh.Lsp does not implement
    // textDocument/foldingRange, so this is the only structural folding source
    // whether or not the language server is running. Not restricted to
    // scheme:file so untitled buffers fold too.
    context.subscriptions.push(
        vscode.languages.registerFoldingRangeProvider(
            { language: "tosh" },
            new ToshFoldingRangeProvider()
        )
    );

    // CodeLens provider
    const configuration = vscode.workspace.getConfiguration("tosh");
    if (configuration.get("codeLens.enabled", true)) {
        context.subscriptions.push(
            vscode.languages.registerCodeLensProvider(selector, new ToshCodeLensProvider())
        );
    }

    // Task provider
    context.subscriptions.push(
        vscode.tasks.registerTaskProvider("tosh", new ToshTaskProvider())
    );

    // Initial LSP start
    setLspState("starting");
    const started = await tryStartLanguageClient(context, selector, lspOutputChannel);
    if (!started) {
        setLspState("stopped");
        lspOutputChannel.appendLine("Using built-in editor providers because the TōSh language server is unavailable.");
        richMetadata = tryLoadRichMetadata(lspOutputChannel);
        if (richMetadata) {
            richMetadataMap = new Map();
            for (const entry of richMetadata) {
                richMetadataMap.set(entry.name, entry);
                if (entry.aliases) {
                    for (const alias of entry.aliases) {
                        richMetadataMap.set(alias, entry);
                    }
                }
            }
            lspOutputChannel.appendLine(`Loaded rich metadata for ${richMetadata.length} commands.`);
        }
        registerLocalProviders(context, selector);
    }

    // Populate commands view. When LSP is running we don't load rich metadata
    // via the fallback path, so run the export asynchronously in the background.
    if (richMetadata) {
        commandsProvider.setMetadata(richMetadata);
    } else {
        loadRichMetadataAsync(lspOutputChannel).then(meta => {
            if (meta) {
                richMetadata = meta;
                richMetadataMap = new Map();
                for (const entry of meta) {
                    richMetadataMap.set(entry.name, entry);
                    if (entry.aliases) for (const a of entry.aliases) richMetadataMap.set(a, entry);
                }
                commandsProvider.setMetadata(meta);
                if (ToshCommandRefPanel._current) ToshCommandRefPanel._current._refresh(meta);
            }
        });
    }

    updateStatusBar();
    statusBarItem.show();
}

function setLspState(state) {
    lspState = state;
    if (serversProvider) serversProvider.refresh();
    if (outlineProvider) outlineProvider.refresh();
    if (statusBarItem) updateStatusBar();
}

function updateStatusBar() {
    const lspIcon = lspState === "running" ? "$(check)" : lspState === "error" ? "$(error)" : lspState === "starting" ? "$(loading~spin)" : "$(circle-slash)";
    statusBarItem.text = `$(terminal) TōSh  LSP ${lspIcon}`;
    statusBarItem.tooltip = `TōSh Language Server: ${lspState}`;
}

async function startLsp(context, selector) {
    if (lspState === "running" || lspState === "starting") {
        return;
    }
    setLspState("starting");
    const started = await tryStartLanguageClient(context, selector, lspOutputChannel);
    if (!started) {
        setLspState("stopped");
    }
}

async function stopLsp() {
    if (!client) return;
    const runningClient = client;
    client = null;
    setLspState("stopped");
    try {
        await runningClient.stop();
    } catch {
        // already stopped
    }
}

function registerTerminalProfile(context) {
    context.subscriptions.push(
        vscode.window.registerTerminalProfileProvider("tosh.terminal", {
            provideTerminalProfile() {
                const configuration = vscode.workspace.getConfiguration("tosh");
                const shellPath = configuration.get("terminal.path", "tosh");

                return {
                    name: "TōSh",
                    shellPath,
                    iconPath: {
                        light: vscode.Uri.joinPath(context.extensionUri, "icons", "tosh-light.svg"),
                        dark: vscode.Uri.joinPath(context.extensionUri, "icons", "tosh-dark.svg")
                    }
                };
            }
        })
    );
}

async function deactivate() {
    if (client) {
        const runningClient = client;
        client = null;
        await runningClient.stop();
    }
}

async function tryStartLanguageClient(context, selector, outputChannel) {
    const configuration = vscode.workspace.getConfiguration("tosh");
    if (!configuration.get("languageServer.enabled", true)) {
        outputChannel.appendLine("TōSh language server is disabled in settings.");
        return false;
    }

    let languageClientModule;
    try {
        languageClientModule = require("vscode-languageclient/node");
    } catch (error) {
        outputChannel.appendLine(`Unable to load vscode-languageclient; falling back to local providers. ${formatError(error)}`);
        return false;
    }

    const serverOptions = findLanguageServerOptions(configuration, outputChannel);
    if (!serverOptions) {
        outputChannel.appendLine("No Tosh.Lsp server could be discovered in the current workspace.");
        return false;
    }

    const { LanguageClient } = languageClientModule;
    const languageClient = new LanguageClient(
        "toshLanguageServer",
        "TōSh Language Server",
        serverOptions,
        {
            documentSelector: [selector],
            outputChannel,
            synchronize: {
                fileEvents: vscode.workspace.createFileSystemWatcher("**/*.tosh")
            },
            errorHandler: {
                error(error, message, count) {
                    if (count < 5) {
                        outputChannel.appendLine(`TōSh language server error (${count}): ${formatError(error)}`);
                        return { action: 1 }; // ErrorAction.Continue
                    }
                    outputChannel.appendLine(`TōSh language server has crashed ${count} times; shutting down.`);
                    setLspState("error");
                    return { action: 2 }; // ErrorAction.Shutdown
                },
                closed() {
                    outputChannel.appendLine("TōSh language server connection closed. Restarting...");
                    return { action: 2, message: "Restarting TōSh language server..." }; // CloseAction.Restart=2
                }
            }
        }
    );

    try {
        context.subscriptions.push(languageClient.start());
        client = languageClient;

        // Track state transitions via the client's state change event
        // vscode-languageclient State enum: Stopped=1, Running=2, Starting=3
        if (languageClient.onDidChangeState) {
            languageClient.onDidChangeState(({ newState }) => {
                if (newState === 2) setLspState("running");
                else if (newState === 1) setLspState("stopped");
                // newState === 3 is Starting — keep current state while transitioning
            });
        }

        outputChannel.appendLine(`Started TōSh language server with: ${describeServerOptions(serverOptions.run)}`);
        return true;
    } catch (error) {
        outputChannel.appendLine(`Failed to start the TōSh language server; falling back to local providers. ${formatError(error)}`);
        return false;
    }
}

function registerLocalProviders(context, selector) {
    context.subscriptions.push(
        vscode.languages.registerCompletionItemProvider(
            selector,
            new ToshCompletionProvider(),
            "$",
            "."
        )
    );

    context.subscriptions.push(
        vscode.languages.registerHoverProvider(selector, new ToshHoverProvider())
    );

    context.subscriptions.push(
        vscode.languages.registerDocumentSymbolProvider(selector, new ToshDocumentSymbolProvider())
    );
}

function loadRichMetadataAsync(outputChannel) {
    return new Promise(resolve => {
        setImmediate(() => resolve(tryLoadRichMetadata(outputChannel)));
    });
}

function tryLoadRichMetadata(outputChannel) {
    const configuration = vscode.workspace.getConfiguration("tosh");
    const dotnetPath = configuration.get("languageServer.dotnetPath", "dotnet");

    for (const workspaceFolder of vscode.workspace.workspaceFolders || []) {
        const root = workspaceFolder.uri.fsPath;
        const cliProject = path.join(root, "src", "Tosh.Cli", "Tosh.Cli.csproj");
        if (!fs.existsSync(cliProject)) {
            continue;
        }

        try {
            const result = childProcess.spawnSync(
                dotnetPath,
                ["run", "--project", cliProject, "--no-build", "--", "--export-command-metadata"],
                { stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 15000, cwd: root }
            );

            if (result.status === 0 && result.stdout) {
                const text = result.stdout.toString("utf-8").trim();
                if (text.startsWith("[")) {
                    return JSON.parse(text);
                }
            }
        } catch (error) {
            outputChannel.appendLine(`Failed to load rich metadata: ${formatError(error)}`);
        }
    }

    return null;
}

function findLanguageServerOptions(configuration, outputChannel) {
    const dotnetPath = configuration.get("languageServer.dotnetPath", "dotnet");
    if (!canRunCommand(dotnetPath, ["--version"])) {
        outputChannel.appendLine(`The configured dotnet executable could not be run: ${dotnetPath}`);
        return null;
    }

    const configuredServerPath = configuration.get("languageServer.serverPath", "").trim();
    if (configuredServerPath.length > 0) {
        const explicitPath = resolveConfiguredPath(configuredServerPath);
        if (!explicitPath || !fs.existsSync(explicitPath)) {
            outputChannel.appendLine(`Configured TōSh language server path was not found: ${configuredServerPath}`);
            return null;
        }

        return createServerOptions(dotnetPath, explicitPath);
    }

    // Prefer the system-installed tosh-lsp binary so editor + CLI stay in lockstep.
    const installedServerCandidates = [
        "/usr/bin/tosh-lsp",
        "/usr/local/bin/tosh-lsp",
        "/bin/tosh-lsp",
    ];
    for (const candidate of installedServerCandidates) {
        if (fs.existsSync(candidate)) {
            return createServerOptions(dotnetPath, candidate);
        }
    }

    for (const workspaceFolder of vscode.workspace.workspaceFolders || []) {
        const root = workspaceFolder.uri.fsPath;
        const builtDllCandidates = [
            path.join(root, "src", "Tosh.Lsp", "bin", "Debug", "net10.0", "Tosh.Lsp.dll"),
            path.join(root, "src", "Tosh.Lsp", "bin", "Release", "net10.0", "Tosh.Lsp.dll")
        ];

        for (const candidate of builtDllCandidates) {
            if (fs.existsSync(candidate)) {
                return createServerOptions(dotnetPath, candidate);
            }
        }

        const projectPath = path.join(root, "src", "Tosh.Lsp", "Tosh.Lsp.csproj");
        if (fs.existsSync(projectPath)) {
            return createServerOptions(dotnetPath, projectPath);
        }
    }

    return null;
}

function createServerOptions(dotnetPath, targetPath) {
    const normalizedPath = path.resolve(targetPath);
    const projectMode = normalizedPath.endsWith(".csproj");

    if (projectMode) {
        const projectDir = path.dirname(normalizedPath);
        const projectName = path.basename(projectDir);
        const dllPath = path.join(projectDir, "bin", "Debug", "net10.0", `${projectName}.dll`);

        // Build first, then use the DLL — avoids dotnet run polluting stdout with build output
        childProcess.spawnSync(dotnetPath, ["build", normalizedPath, "-v", "q"], {
            stdio: "ignore",
            windowsHide: true,
            cwd: projectDir
        });

        if (fs.existsSync(dllPath)) {
            const commandOptions = {
                command: dotnetPath,
                args: [dllPath, "--stdio"],
                options: { cwd: projectDir }
            };
            return { run: commandOptions, debug: commandOptions };
        }
    }

    // Native executable — run directly without dotnet
    if (!normalizedPath.endsWith(".dll")) {
        const commandOptions = {
            command: normalizedPath,
            args: ["--stdio"],
            options: { cwd: path.dirname(normalizedPath) }
        };
        return { run: commandOptions, debug: commandOptions };
    }

    const args = [normalizedPath, "--stdio"];
    const commandOptions = {
        command: dotnetPath,
        args,
        options: {
            cwd: path.dirname(normalizedPath)
        }
    };

    return {
        run: commandOptions,
        debug: commandOptions
    };
}

function resolveConfiguredPath(configuredPath) {
    if (path.isAbsolute(configuredPath)) {
        return configuredPath;
    }

    const firstWorkspace = vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders[0];
    if (!firstWorkspace) {
        return null;
    }

    return path.resolve(firstWorkspace.uri.fsPath, configuredPath);
}

function canRunCommand(command, args) {
    const result = childProcess.spawnSync(command, args, {
        stdio: "ignore",
        windowsHide: true
    });

    return result.status === 0;
}

function describeServerOptions(serverOptions) {
    const argsText = Array.isArray(serverOptions.args) ? serverOptions.args.join(" ") : "";
    return `${serverOptions.command} ${argsText}`.trim();
}

function formatError(error) {
    if (!error) {
        return "";
    }

    if (typeof error === "string") {
        return error;
    }

    if (error.message) {
        return error.message;
    }

    return String(error);
}

// ─── Sidebar: Servers view ────────────────────────────────────────────────────

class ToshServersProvider {
    constructor() {
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
    }

    refresh() {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element) {
        return element;
    }

    getChildren(element) {
        if (element) return [];
        return [
            this._buildLspItem(),
            this._buildMcpItem()
        ];
    }

    _buildLspItem() {
        const stateLabel = { stopped: "Stopped", starting: "Starting…", running: "Running", error: "Error" }[lspState] || lspState;
        const iconName = { stopped: "circle-slash", starting: "loading~spin", running: "check", error: "error" }[lspState] || "circle-slash";

        const item = new vscode.TreeItem("LSP");
        item.description = stateLabel;
        item.iconPath = new vscode.ThemeIcon(iconName);
        item.contextValue = `lsp${capitalize(lspState)}`;
        item.tooltip = `TōSh Language Server — ${stateLabel}`;
        item.collapsibleState = vscode.TreeItemCollapsibleState.None;
        return item;
    }

    _buildMcpItem() {
        const item = new vscode.TreeItem("MCP");
        item.description = "External";
        item.iconPath = new vscode.ThemeIcon("plug");
        item.contextValue = "mcpExternal";
        item.tooltip = "TōSh MCP Server — managed by Claude Code MCP configuration";
        item.collapsibleState = vscode.TreeItemCollapsibleState.None;
        return item;
    }
}

// ─── Utilities ────────────────────────────────────────────────────────────────

function capitalize(str) {
    return str.charAt(0).toUpperCase() + str.slice(1);
}

// ─── DAP ─────────────────────────────────────────────────────────────────────

function findDapServerPath() {
    const configuration = vscode.workspace.getConfiguration("tosh");
    const dotnetPath = configuration.get("languageServer.dotnetPath", "dotnet");

    const configuredPath = configuration.get("dap.serverPath", "").trim();
    if (configuredPath.length > 0) {
        const resolved = resolveConfiguredPath(configuredPath);
        if (resolved && fs.existsSync(resolved)) {
            return resolved.endsWith(".dll")
                ? { command: dotnetPath, args: [resolved] }
                : { command: resolved, args: [] };
        }
    }

    // Prefer system-installed tosh-dap binary
    const installedCandidates = [
        "/usr/bin/tosh-dap",
        "/usr/local/bin/tosh-dap",
        path.join(process.env.HOME || "", ".local", "bin", "tosh-dap")
    ];
    for (const candidate of installedCandidates) {
        if (fs.existsSync(candidate)) {
            return { command: candidate, args: [] };
        }
    }

    for (const workspaceFolder of vscode.workspace.workspaceFolders || []) {
        const root = workspaceFolder.uri.fsPath;
        for (const candidate of [
            path.join(root, "src", "Tosh.Dap", "bin", "Debug", "net10.0", "Tosh.Dap.dll"),
            path.join(root, "src", "Tosh.Dap", "bin", "Release", "net10.0", "Tosh.Dap.dll")
        ]) {
            if (fs.existsSync(candidate)) return { command: dotnetPath, args: [candidate] };
        }

        const proj = path.join(root, "src", "Tosh.Dap", "Tosh.Dap.csproj");
        if (fs.existsSync(proj)) {
            childProcess.spawnSync(dotnetPath, ["build", proj, "-v", "q"], {
                stdio: "ignore", windowsHide: true, cwd: path.dirname(proj)
            });
            const dll = path.join(path.dirname(proj), "bin", "Debug", "net10.0", "Tosh.Dap.dll");
            if (fs.existsSync(dll)) return { command: dotnetPath, args: [dll] };
        }
    }

    return null;
}

class ToshDebugAdapterDescriptorFactory {
    createDebugAdapterDescriptor(_session) {
        const result = findDapServerPath();
        if (!result) {
            vscode.window.showErrorMessage(
                "TōSh DAP server not found. Please build the project first (src/Tosh.Dap)."
            );
            return null;
        }
        return new vscode.DebugAdapterExecutable(result.command, result.args || []);
    }
}

async function runActiveFile(forcePrompt = false) {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== "tosh") {
        vscode.window.showWarningMessage("Open a .tosh file to run it.");
        return;
    }
    const filePath = editor.document.uri.fsPath;
    await runScript(filePath, forcePrompt);
}

function scriptUsesArgs(filePath) {
    try {
        const text = fs.readFileSync(filePath, "utf-8");
        return /\$args\b/.test(text);
    } catch { return false; }
}

async function promptForArgs(filePath) {
    const lastName = path.basename(filePath);
    const last = _argsCache.get(filePath) || "";
    const result = await vscode.window.showInputBox({
        title: `Run ${lastName}`,
        prompt: "Arguments (space-separated)",
        value: last,
        placeHolder: "arg1 arg2 ..."
    });
    if (result === undefined) return null; // cancelled
    _argsCache.set(filePath, result);
    return result;
}

async function runScript(filePath, forcePrompt = false) {
    const configuration = vscode.workspace.getConfiguration("tosh");
    const shellPath = configuration.get("terminal.path", "tosh");

    let args = "";
    if (forcePrompt || scriptUsesArgs(filePath)) {
        const prompted = await promptForArgs(filePath);
        if (prompted === null) return; // user cancelled
        args = prompted;
    }

    const terminal = findOrCreateToshTerminal();
    terminal.show();
    const cmd = args.trim()
        ? `${shellPath} "${filePath}" ${args}`
        : `${shellPath} "${filePath}"`;
    terminal.sendText(cmd);
}

async function debugActiveFile() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== "tosh") {
        vscode.window.showWarningMessage("Open a .tosh file to debug it.");
        return;
    }

    const filePath = editor.document.uri.fsPath;
    const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);

    await vscode.debug.startDebugging(folder, {
        type: "tosh",
        request: "launch",
        name: "Debug TōSh Script",
        program: filePath,
        args: [],
        stopOnEntry: false
    });
}

async function runScriptNode(node) {
    if (!node || !node.resourceUri) return;
    await runScript(node.resourceUri.fsPath);
}

function findOrCreateToshTerminal() {
    const configuration = vscode.workspace.getConfiguration("tosh");
    const shellPath = configuration.get("terminal.path", "tosh");
    for (const t of vscode.window.terminals) {
        if (t.name === "TōSh" || t.name === "TōSh: Run") {
            return t;
        }
    }
    return vscode.window.createTerminal({ name: "TōSh", shellPath });
}

function runSelection() {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    const sel = editor.selection;
    const text = sel.isEmpty
        ? editor.document.lineAt(sel.active.line).text.trim()
        : editor.document.getText(sel).trim();
    if (!text) return;
    const terminal = findOrCreateToshTerminal();
    terminal.show();
    terminal.sendText(text);
}

// ─── Sidebar: Solution view (.slnx XML format) ────────────────────────────────

// Minimal attribute extractor — no external XML parser needed for these simple formats.
function xmlAttrs(tag) {
    const result = {};
    const re = /(\w+)="([^"]*)"/g;
    let m;
    while ((m = re.exec(tag)) !== null) result[m[1]] = m[2];
    return result;
}

// Parse a .slnx file into a list of { folder, projPath } entries.
function parseSlnx(slnxPath) {
    let xml;
    try { xml = fs.readFileSync(slnxPath, "utf-8"); } catch { return []; }
    const slnDir = path.dirname(slnxPath);
    const entries = [];
    let currentFolder = "/";
    // Process tags line by line for simplicity
    const tagRe = /<(\/?)(Folder|Project)([^>]*)\/?>|<\/(Folder)>/g;
    let m;
    const folderStack = ["/"];
    while ((m = tagRe.exec(xml)) !== null) {
        const [, closing, tag, attrs] = m;
        if (tag === "Folder" && !closing) {
            const { Name } = xmlAttrs(attrs);
            folderStack.push(Name || "/");
        } else if (m[4] === "Folder") {
            folderStack.pop();
        } else if (tag === "Project") {
            const { Path: relPath } = xmlAttrs(attrs);
            if (relPath) {
                entries.push({
                    folder: folderStack[folderStack.length - 1],
                    projPath: path.resolve(slnDir, relPath)
                });
            }
        }
    }
    return entries;
}

// Generate a minimal .slnx skeleton
function makeSlnx(projectEntries) {
    const byFolder = new Map();
    for (const { folder, relPath } of projectEntries) {
        if (!byFolder.has(folder)) byFolder.set(folder, []);
        byFolder.get(folder).push(relPath);
    }
    const lines = ["<Solution>"];
    for (const [folder, paths] of byFolder) {
        lines.push(`  <Folder Name="${folder}">`);
        for (const p of paths) lines.push(`    <Project Path="${p}" />`);
        lines.push(`  </Folder>`);
    }
    lines.push("</Solution>");
    return lines.join("\n") + "\n";
}

class ToshSolutionProvider {
    constructor(context) {
        this._context = context;
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
        this._watchers = [];
        this._diagMap = new Map(); // fsPath → { errors, warnings }
        this._initWatchers();

        // Track LSP diagnostics on .tosh files
        this._watchers.push(
            vscode.languages.onDidChangeDiagnostics(e => {
                let relevant = false;
                for (const uri of e.uris) {
                    if (uri.fsPath.endsWith(".tosh")) {
                        this._updateFileDiag(uri);
                        relevant = true;
                    }
                }
                if (relevant) this.refresh();
            })
        );
    }

    dispose() {
        for (const w of this._watchers) w.dispose();
    }

    _initWatchers() {
        const onchange = () => this.refresh();
        for (const glob of ["**/*.slnx", "**/*.toshproj", "**/*.tosh"]) {
            const w = vscode.workspace.createFileSystemWatcher(glob);
            w.onDidCreate(onchange);
            w.onDidDelete(onchange);
            w.onDidChange(onchange);
            this._watchers.push(w);
        }
    }

    refresh() { this._onDidChangeTreeData.fire(); }

    getTreeItem(element) { return element; }

    async getChildren(element) {
        if (!element) return this._getSolutions();
        if (element.contextValue === "solution") return this._getFoldersAndProjects(element);
        if (element.contextValue === "solutionFolder") return this._getFolderProjects(element);
        if (element.contextValue === "project") return this._getProjectFiles(element);
        return [];
    }

    _updateFileDiag(uri) {
        const diags = vscode.languages.getDiagnostics(uri);
        const errors = diags.filter(d => d.severity === vscode.DiagnosticSeverity.Error).length;
        const warnings = diags.filter(d => d.severity === vscode.DiagnosticSeverity.Warning).length;
        if (errors === 0 && warnings === 0) this._diagMap.delete(uri.fsPath);
        else this._diagMap.set(uri.fsPath, { errors, warnings });
    }

    _getDirDiag(dir) {
        let errors = 0, warnings = 0;
        const prefix = dir + path.sep;
        for (const [p, d] of this._diagMap) {
            if (p.startsWith(prefix)) { errors += d.errors; warnings += d.warnings; }
        }
        return { errors, warnings };
    }

    _applyDiagToItem(item, errors, warnings) {
        if (errors > 0) {
            item.iconPath = new vscode.ThemeIcon("error", new vscode.ThemeColor("list.errorForeground"));
            item.description = `${errors} error${errors > 1 ? "s" : ""}${warnings > 0 ? `, ${warnings} warning${warnings > 1 ? "s" : ""}` : ""}`;
        } else if (warnings > 0) {
            item.iconPath = new vscode.ThemeIcon("warning", new vscode.ThemeColor("list.warningForeground"));
            item.description = `${warnings} warning${warnings > 1 ? "s" : ""}`;
        }
    }

    async _getSolutions() {
        const uris = await vscode.workspace.findFiles("**/*.slnx", "**/node_modules/**");
        if (uris.length === 0) {
            const p = new vscode.TreeItem("No solution (.slnx) found in workspace.");
            p.contextValue = "empty";
            return [p];
        }
        return uris.map(uri => {
            const item = new vscode.TreeItem(path.basename(uri.fsPath), vscode.TreeItemCollapsibleState.Expanded);
            item.resourceUri = uri;
            item.contextValue = "solution";
            item.iconPath = new vscode.ThemeIcon("symbol-namespace");
            item.tooltip = uri.fsPath;
            item.slnPath = uri.fsPath;
            item._entries = parseSlnx(uri.fsPath);
            return item;
        });
    }

    _getFoldersAndProjects(solutionNode) {
        const entries = solutionNode._entries || [];
        const folders = [...new Set(entries.map(e => e.folder))];
        // If only root folder, flatten directly to projects
        if (folders.length === 1 && folders[0] === "/") {
            return this._makeProjectNodes(entries, solutionNode.slnPath);
        }
        return folders.map(folder => {
            const item = new vscode.TreeItem(folder, vscode.TreeItemCollapsibleState.Expanded);
            item.contextValue = "solutionFolder";
            item.iconPath = new vscode.ThemeIcon("folder");
            item._entries = entries.filter(e => e.folder === folder);
            item.slnPath = solutionNode.slnPath;
            return item;
        });
    }

    _getFolderProjects(folderNode) {
        return this._makeProjectNodes(folderNode._entries || [], folderNode.slnPath);
    }

    _makeProjectNodes(entries, slnPath) {
        return entries.filter(({ projPath }) => projPath.endsWith(".toshproj")).map(({ projPath }) => {
            const ext = path.extname(projPath);
            const label = path.basename(projPath, ext);
            const exists = fs.existsSync(projPath);
            const projDir = path.dirname(projPath);
            const item = new vscode.TreeItem(label, exists ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
            item.resourceUri = vscode.Uri.file(projPath);
            item.contextValue = "project";
            item.iconPath = new vscode.ThemeIcon(exists ? "package" : "warning");
            item.description = "TōSh";
            item.tooltip = projPath;
            item.projPath = projPath;
            item.projDir = projDir;
            item.slnPath = slnPath;
            const { errors, warnings } = this._getDirDiag(projDir);
            this._applyDiagToItem(item, errors, warnings);
            return item;
        });
    }

    _getProjectFiles(projNode) {
        // SDK-style implicit inclusion: show all .tosh files in the project directory
        const dir = projNode.projDir;
        if (!fs.existsSync(dir)) return [];
        const files = fs.readdirSync(dir).filter(f => f.endsWith(".tosh"));
        if (files.length === 0) {
            const p = new vscode.TreeItem("No .tosh files");
            p.contextValue = "empty";
            return [p];
        }
        return files.map(f => {
            const filePath = path.join(dir, f);
            const item = new vscode.TreeItem(f);
            item.resourceUri = vscode.Uri.file(filePath);
            item.contextValue = "projectFile";
            item.iconPath = new vscode.ThemeIcon("file-code");
            item.projPath = projNode.projPath;
            item.command = { command: "vscode.open", title: "Open", arguments: [vscode.Uri.file(filePath)] };
            const diag = this._diagMap.get(filePath);
            if (diag) this._applyDiagToItem(item, diag.errors, diag.warnings);
            return item;
        });
    }

    async newSolution() {
        const name = await vscode.window.showInputBox({ prompt: "Solution name", placeHolder: "MyApp" });
        if (!name) return;
        const folder = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: "Select Folder" });
        if (!folder || folder.length === 0) return;
        const slnPath = path.join(folder[0].fsPath, `${name}.slnx`);
        fs.writeFileSync(slnPath, makeSlnx([]));
        this.refresh();
        vscode.window.showInformationMessage(`Created ${path.basename(slnPath)}`);
    }

    async newProject(node) {
        const slnPath = node ? node.slnPath : await this._pickSolution();
        if (!slnPath) return;

        const name = await vscode.window.showInputBox({ prompt: "Project name", placeHolder: "MyProject" });
        if (!name) return;

        const slnDir = path.dirname(slnPath);
        const projDir = path.join(slnDir, name);
        const projPath = path.join(projDir, `${name}.toshproj`);
        const mainFile = path.join(projDir, "main.tosh");

        fs.mkdirSync(projDir, { recursive: true });
        // Write a minimal MSBuild-style .toshproj
        fs.writeFileSync(projPath, `<Project Sdk="TōSh.Sdk">

  <PropertyGroup>
    <OutputType>Script</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

</Project>
`);
        fs.writeFileSync(mainFile, `# ${name}\n`);

        // Add project to .slnx
        const entries = parseSlnx(slnPath);
        const rel = path.relative(slnDir, projPath).replace(/\\/g, "/");
        entries.push({ folder: "/", relPath: rel });
        // Rebuild slnx — convert projPath-based entries to relPath-based
        const slnxEntries = entries.map(e =>
            e.relPath ? e : { folder: e.folder, relPath: path.relative(slnDir, e.projPath).replace(/\\/g, "/") }
        );
        fs.writeFileSync(slnPath, makeSlnx(slnxEntries));

        this.refresh();
        vscode.window.showTextDocument(vscode.Uri.file(mainFile));
    }

    async addFile(node) {
        if (!node) return;
        const name = await vscode.window.showInputBox({ prompt: "File name", placeHolder: "module.tosh" });
        if (!name) return;
        const fileName = name.endsWith(".tosh") ? name : `${name}.tosh`;
        const filePath = path.join(node.projDir, fileName);
        if (!fs.existsSync(filePath)) fs.writeFileSync(filePath, "");
        this.refresh();
        vscode.window.showTextDocument(vscode.Uri.file(filePath));
    }

    async removeNode(node) {
        if (!node) return;
        const label = node.contextValue === "project"
            ? `Remove ${path.basename(node.projPath, ".toshproj")} from solution?`
            : `Remove ${path.basename(node.resourceUri.fsPath)} from project?`;
        const confirm = await vscode.window.showWarningMessage(label, { modal: true }, "Remove");
        if (confirm !== "Remove") return;

        if (node.contextValue === "project") {
            const entries = parseSlnx(node.slnPath);
            const slnDir = path.dirname(node.slnPath);
            const filtered = entries.filter(e => e.projPath !== node.projPath);
            const relEntries = filtered.map(e => ({ folder: e.folder, relPath: path.relative(slnDir, e.projPath).replace(/\\/g, "/") }));
            fs.writeFileSync(node.slnPath, makeSlnx(relEntries));
        } else if (node.contextValue === "projectFile") {
            const answer = await vscode.window.showWarningMessage(
                `Also delete ${path.basename(node.resourceUri.fsPath)} from disk?`, { modal: true }, "Delete File", "Remove Only");
            if (answer === "Delete File") fs.unlinkSync(node.resourceUri.fsPath);
        }
        this.refresh();
    }

    buildProject(node) {
        if (!node) return;
        const terminal = vscode.window.createTerminal({ name: "TōSh: Build" });
        terminal.show();
        terminal.sendText(`dotnet build "${node.projPath}"`);
    }

    buildSolution(node) {
        if (!node) return;
        const terminal = vscode.window.createTerminal({ name: "TōSh: Build" });
        terminal.show();
        terminal.sendText(`dotnet build "${node.slnPath}"`);
    }

    async _pickSolution() {
        const uris = await vscode.workspace.findFiles("**/*.slnx", "**/node_modules/**");
        if (uris.length === 0) return null;
        if (uris.length === 1) return uris[0].fsPath;
        const items = uris.map(u => ({ label: path.basename(u.fsPath), detail: u.fsPath, fsPath: u.fsPath }));
        const pick = await vscode.window.showQuickPick(items, { placeHolder: "Select solution" });
        return pick ? pick.fsPath : null;
    }
}

// ─── Sidebar: Document Outline view ───────────────────────────────────────────

class ToshOutlineProvider {
    constructor(context) {
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
        this.sortAlphabetically = false;

        context.subscriptions.push(
            vscode.window.onDidChangeActiveTextEditor(() => this.refresh())
        );
        context.subscriptions.push(
            vscode.workspace.onDidChangeTextDocument(e => {
                if (vscode.window.activeTextEditor && e.document === vscode.window.activeTextEditor.document) {
                    this.refresh();
                }
            })
        );
    }

    toggleSort() {
        this.sortAlphabetically = !this.sortAlphabetically;
        this.refresh();
    }

    refresh() { this._onDidChangeTreeData.fire(); }

    getTreeItem(element) { return element; }

    async getChildren(element) {
        if (element) {
            return (element.children || []).map(child => this._createSymbolTreeItem(child));
        }

        const editor = vscode.window.activeTextEditor;
        if (!editor || (editor.document.languageId !== "tosh" && editor.document.languageId !== "tome")) {
            const placeholder = new vscode.TreeItem("No active TōSh script.");
            placeholder.contextValue = "empty";
            return [placeholder];
        }

        let symbols = null;
        if (lspState === "running") {
            try {
                const fetchPromise = vscode.commands.executeCommand(
                    "vscode.executeDocumentSymbolProvider",
                    editor.document.uri
                );
                const timeoutPromise = new Promise(resolve => setTimeout(() => resolve(null), 400));
                symbols = await Promise.race([fetchPromise, timeoutPromise]);
            } catch { }
        }

        if (!symbols || symbols.length === 0) {
            const fallbackProvider = new ToshDocumentSymbolProvider();
            symbols = fallbackProvider.provideDocumentSymbols(editor.document);
        }

        if (!symbols || symbols.length === 0) {
            const placeholder = new vscode.TreeItem("No symbols found in current script.");
            placeholder.contextValue = "empty";
            return [placeholder];
        }

        let items = symbols.map(sym => this._createSymbolTreeItem(sym));
        if (this.sortAlphabetically) {
            items = items.sort((a, b) => String(a.label).localeCompare(String(b.label)));
        }
        return items;
    }

    _createSymbolTreeItem(sym) {
        const hasChildren = sym.children && sym.children.length > 0;
        const item = new vscode.TreeItem(
            sym.name,
            hasChildren ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None
        );
        item.description = sym.detail || "";
        item.iconPath = this._getSymbolIcon(sym.kind);
        item.children = sym.children || [];
        item.contextValue = (sym.kind === vscode.SymbolKind.Function || sym.kind === vscode.SymbolKind.Method) ? 'function' : 'symbol';

        const tooltipMd = new vscode.MarkdownString();
        tooltipMd.appendCodeblock(`${sym.name} ${sym.detail || ''}`, 'tosh');
        if (sym.docComment) {
            tooltipMd.appendMarkdown(`\n*${sym.docComment}*`);
        }
        item.tooltip = tooltipMd;

        const selectionRange = sym.selectionRange || sym.range;
        if (selectionRange && vscode.window.activeTextEditor) {
            item.command = {
                command: "vscode.open",
                title: "Jump to Symbol",
                arguments: [
                    vscode.window.activeTextEditor.document.uri,
                    { selection: selectionRange }
                ]
            };
        }
        return item;
    }

    _getSymbolIcon(kind) {
        switch (kind) {
            case vscode.SymbolKind.Module: return new vscode.ThemeIcon("symbol-module");
            case vscode.SymbolKind.Class: return new vscode.ThemeIcon("symbol-class");
            case vscode.SymbolKind.Struct: return new vscode.ThemeIcon("symbol-struct");
            case vscode.SymbolKind.Enum: return new vscode.ThemeIcon("symbol-enum");
            case vscode.SymbolKind.EnumMember: return new vscode.ThemeIcon("symbol-enum-member");
            case vscode.SymbolKind.Function: return new vscode.ThemeIcon("symbol-function");
            case vscode.SymbolKind.Method: return new vscode.ThemeIcon("symbol-method");
            case vscode.SymbolKind.Property: return new vscode.ThemeIcon("symbol-property");
            case vscode.SymbolKind.Field: return new vscode.ThemeIcon("symbol-field");
            case vscode.SymbolKind.Variable: return new vscode.ThemeIcon("symbol-variable");
            case vscode.SymbolKind.Constant: return new vscode.ThemeIcon("symbol-constant");
            case vscode.SymbolKind.Interface: return new vscode.ThemeIcon("symbol-interface");
            default: return new vscode.ThemeIcon("symbol-misc");
        }
    }
}

// ─── Sidebar: Library & Script Object Browser ─────────────────────────

class ToshLibraryExplorerProvider {
    constructor(context) {
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
    }

    refresh() { this._onDidChangeTreeData.fire(); }

    getTreeItem(element) { return element; }

    async getChildren(element) {
        if (element) {
            if (element.type === "dir") {
                return this._getDirectoryChildren(element.path);
            }
            if (element.type === "file") {
                return this._getFileSymbols(element.path);
            }
            if (element.children) {
                return element.children.map(child => this._createSymbolTreeItem(child, element.filePath));
            }
            return [];
        }

        const roots = [];
        const homedir = os.homedir();
        const userLib = path.join(homedir, ".config", "tosh", "lib");
        if (fs.existsSync(userLib)) {
            const item = new vscode.TreeItem("Library (~/.config/tosh/lib)", vscode.TreeItemCollapsibleState.Expanded);
            item.type = "dir";
            item.path = userLib;
            item.iconPath = new vscode.ThemeIcon("library");
            roots.push(item);
        }

        if (vscode.workspace.workspaceFolders) {
            for (const folder of vscode.workspace.workspaceFolders) {
                const item = new vscode.TreeItem(folder.name, vscode.TreeItemCollapsibleState.Expanded);
                item.type = "dir";
                item.path = folder.uri.fsPath;
                item.iconPath = new vscode.ThemeIcon("folder");
                roots.push(item);
            }
        }

        return roots;
    }

    async _getDirectoryChildren(dirPath) {
        try {
            const entries = fs.readdirSync(dirPath, { withFileTypes: true });
            const items = [];
            for (const entry of entries) {
                if (entry.name.startsWith(".")) continue;
                const fullPath = path.join(dirPath, entry.name);
                if (entry.isDirectory()) {
                    const item = new vscode.TreeItem(entry.name, vscode.TreeItemCollapsibleState.Collapsed);
                    item.type = "dir";
                    item.path = fullPath;
                    item.iconPath = new vscode.ThemeIcon("folder");
                    items.push(item);
                } else if (entry.isFile() && entry.name.endsWith(".tosh")) {
                    const item = new vscode.TreeItem(entry.name, vscode.TreeItemCollapsibleState.Collapsed);
                    item.type = "file";
                    item.path = fullPath;
                    item.iconPath = new vscode.ThemeIcon("file-code");
                    items.push(item);
                }
            }
            return items;
        } catch {
            return [];
        }
    }

    async _getFileSymbols(filePath) {
        try {
            const uri = vscode.Uri.file(filePath);
            const doc = await vscode.workspace.openTextDocument(uri);
            
            let symbols = null;
            if (lspState === "running") {
                try {
                    symbols = await vscode.commands.executeCommand("vscode.executeDocumentSymbolProvider", uri);
                } catch { }
            }
            if (!symbols || symbols.length === 0) {
                const fallbackProvider = new ToshDocumentSymbolProvider();
                symbols = fallbackProvider.provideDocumentSymbols(doc);
            }

            if (!symbols || symbols.length === 0) {
                return [new vscode.TreeItem("No symbols declared", vscode.TreeItemCollapsibleState.None)];
            }

            return symbols.map(sym => this._createSymbolTreeItem(sym, filePath));
        } catch {
            return [new vscode.TreeItem("Error loading symbols", vscode.TreeItemCollapsibleState.None)];
        }
    }

    _createSymbolTreeItem(sym, filePath) {
        const hasChildren = sym.children && sym.children.length > 0;
        const item = new vscode.TreeItem(
            sym.name,
            hasChildren ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None
        );
        item.description = sym.detail || "";
        item.iconPath = this._getSymbolIcon(sym.kind);
        item.children = sym.children || [];
        item.filePath = filePath;
        item.contextValue = (sym.kind === vscode.SymbolKind.Function || sym.kind === vscode.SymbolKind.Method) ? 'function' : 'symbol';

        const tooltipMd = new vscode.MarkdownString();
        tooltipMd.appendCodeblock(`${sym.name} ${sym.detail || ''}`, 'tosh');
        if (sym.docComment) {
            tooltipMd.appendMarkdown(`\n*${sym.docComment}*`);
        }
        item.tooltip = tooltipMd;

        const range = sym.selectionRange || sym.range;
        if (range && filePath) {
            item.command = {
                command: "vscode.open",
                title: "Open Symbol",
                arguments: [
                    vscode.Uri.file(filePath),
                    { selection: range }
                ]
            };
        }
        return item;
    }

    _getSymbolIcon(kind) {
        switch (kind) {
            case vscode.SymbolKind.Module: return new vscode.ThemeIcon("symbol-module");
            case vscode.SymbolKind.Class: return new vscode.ThemeIcon("symbol-class");
            case vscode.SymbolKind.Interface: return new vscode.ThemeIcon("symbol-interface");
            case vscode.SymbolKind.Struct: return new vscode.ThemeIcon("symbol-struct");
            case vscode.SymbolKind.Enum: return new vscode.ThemeIcon("symbol-enum");
            case vscode.SymbolKind.EnumMember: return new vscode.ThemeIcon("symbol-enum-member");
            case vscode.SymbolKind.Function: return new vscode.ThemeIcon("symbol-function");
            case vscode.SymbolKind.Method: return new vscode.ThemeIcon("symbol-method");
            case vscode.SymbolKind.Property: return new vscode.ThemeIcon("symbol-property");
            case vscode.SymbolKind.Field: return new vscode.ThemeIcon("symbol-field");
            default: return new vscode.ThemeIcon("symbol-misc");
        }
    }
}

// ─── Sidebar: Dependencies & Imports view ────────────────────────────

class ToshDependenciesProvider {
    constructor(context) {
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
        context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(() => this.refresh()));
    }

    refresh() { this._onDidChangeTreeData.fire(); }

    getTreeItem(element) { return element; }

    async getChildren(element) {
        if (element) return element.children || [];

        const editor = vscode.window.activeTextEditor;
        if (!editor || (editor.document.languageId !== "tosh" && editor.document.languageId !== "tome")) {
            return [new vscode.TreeItem("No active TōSh script.")];
        }

        const text = editor.document.getText();
        const lines = text.split(/\r?\n/);
        const requires = [];

        lines.forEach((line, index) => {
            const match = line.match(/^\s*require\s+(?:"([^"]+)"|'([^']+)'|([A-Za-z0-9_.-]+))/);
            if (match) {
                const target = match[1] || match[2] || match[3];
                const item = new vscode.TreeItem(target, vscode.TreeItemCollapsibleState.None);
                item.description = `Line ${index + 1}`;
                item.iconPath = new vscode.ThemeIcon("references");
                item.command = {
                    command: "vscode.open",
                    title: "Jump to Require",
                    arguments: [editor.document.uri, { selection: new vscode.Range(index, 0, index, line.length) }]
                };
                requires.push(item);
            }
        });

        if (requires.length === 0) {
            return [new vscode.TreeItem("No require statements in script.")];
        }

        return requires;
    }
}

// ─── Sidebar: Scripts view ────────────────────────────────────────────────────

class ToshScriptsProvider {
    constructor() {
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
        this._watcher = vscode.workspace.createFileSystemWatcher("**/*.tosh");
        this._watcher.onDidCreate(() => this.refresh());
        this._watcher.onDidDelete(() => this.refresh());
    }

    dispose() { this._watcher.dispose(); }

    refresh() { this._onDidChangeTreeData.fire(); }

    getTreeItem(element) { return element; }

    async getChildren() {
        const allTosh = await vscode.workspace.findFiles("**/*.tosh", "**/node_modules/**");

        // Exclude files already tracked in any .toshproj
        const projUris = await vscode.workspace.findFiles("**/*.toshproj", "**/node_modules/**");
        const trackedFiles = new Set();
        for (const projUri of projUris) {
            try {
                const data = JSON.parse(fs.readFileSync(projUri.fsPath, "utf-8"));
                const projDir = path.dirname(projUri.fsPath);
                for (const rel of (data.files || [])) {
                    trackedFiles.add(path.resolve(projDir, rel));
                }
            } catch { /* skip malformed */ }
        }

        const loose = allTosh.filter(u => !trackedFiles.has(u.fsPath));
        if (loose.length === 0) {
            const placeholder = new vscode.TreeItem("No loose .tosh scripts found.");
            placeholder.contextValue = "empty";
            return [placeholder];
        }

        return loose.map(uri => {
            const item = new vscode.TreeItem(path.basename(uri.fsPath), vscode.TreeItemCollapsibleState.None);
            item.resourceUri = uri;
            item.contextValue = "script";
            item.iconPath = new vscode.ThemeIcon("file-code");
            item.tooltip = uri.fsPath;
            item.description = vscode.workspace.asRelativePath(path.dirname(uri.fsPath));
            item.command = { command: "vscode.open", title: "Open", arguments: [uri] };
            return item;
        });
    }
}

// ─── Sidebar: Commands view ───────────────────────────────────────────────────

class ToshCommandsProvider {
    constructor() {
        this._onDidChangeTreeData = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._onDidChangeTreeData.event;
        this._metadata = [];
        this._byCategory = new Map();
    }

    setMetadata(metadata) {
        this._metadata = metadata;
        this._byCategory = new Map();
        for (const entry of metadata) {
            const cat = entry.category || "Other";
            if (!this._byCategory.has(cat)) this._byCategory.set(cat, []);
            this._byCategory.get(cat).push(entry);
        }
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element) { return element; }

    getChildren(element) {
        if (!element) {
            if (this._byCategory.size === 0) {
                const p = new vscode.TreeItem("Language server unavailable — command list not loaded.");
                p.contextValue = "empty";
                return [p];
            }
            return [...this._byCategory.keys()].sort().map(cat => {
                const item = new vscode.TreeItem(cat, vscode.TreeItemCollapsibleState.Collapsed);
                item.contextValue = "commandCategory";
                item.iconPath = new vscode.ThemeIcon("symbol-namespace");
                item.description = `${this._byCategory.get(cat).length}`;
                item._category = cat;
                return item;
            });
        }

        if (element.contextValue === "commandCategory") {
            const entries = this._byCategory.get(element._category) || [];
            return entries.map(entry => {
                const item = new vscode.TreeItem(entry.name);
                item.description = entry.description;
                item.contextValue = "command";
                item.iconPath = new vscode.ThemeIcon(entry.isExperimental ? "beaker" : entry.deprecatedVersion ? "warning" : "symbol-function");
                item.tooltip = new vscode.MarkdownString(`**${entry.name}**\n\n${entry.description}`);
                item.command = {
                    command: "tosh.openCommandRef",
                    title: "Open Command Reference",
                    arguments: [entry.name]
                };
                return item;
            });
        }

        return [];
    }
}

// ─── Command Reference WebviewPanel ──────────────────────────────────────────

class ToshCommandRefPanel {
    static _current = null;

    static show(context, commandName) {
        if (ToshCommandRefPanel._current) {
            ToshCommandRefPanel._current._panel.reveal();
            if (commandName) ToshCommandRefPanel._current._scrollTo(commandName);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            "tosh.commandRef",
            "TōSh Command Reference",
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        ToshCommandRefPanel._current = new ToshCommandRefPanel(panel, context);
        if (commandName) ToshCommandRefPanel._current._scrollTo(commandName);
    }

    constructor(panel, context) {
        this._panel = panel;
        this._context = context;
        this._panel.webview.html = this._buildHtml(richMetadata || []);

        this._panel.webview.onDidReceiveMessage(msg => {
            if (msg.type === "open") {
                vscode.workspace.openTextDocument(vscode.Uri.file(msg.path))
                    .then(doc => vscode.window.showTextDocument(doc));
            }
        });

        this._panel.onDidDispose(() => { ToshCommandRefPanel._current = null; });
    }

    _refresh(metadata) {
        this._panel.webview.html = this._buildHtml(metadata);
    }

    _scrollTo(commandName) {
        this._panel.webview.postMessage({ type: "scrollTo", name: commandName });
    }

    _buildHtml(metadata) {
        const entriesJson = JSON.stringify(metadata.map(e => ({
            name: e.name,
            category: e.category || "Other",
            description: e.description || "",
            longDescription: e.longDescription || "",
            usage: e.usage || "",
            aliases: e.aliases || [],
            isExperimental: !!e.isExperimental,
            deprecatedVersion: e.deprecatedVersion || null,
            removedVersion: e.removedVersion || null,
            sinceVersion: e.sinceVersion || null,
            arguments: e.arguments || [],
            options: e.options || [],
            examples: e.examples || [],
            tags: e.tags || [],
            seeAlso: e.seeAlso || [],
            output: e.output || "",
        })));

        return /* html */`<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>TōSh Command Reference</title>
<style>
  :root {
    --card-bg: var(--vscode-editor-inactiveSelectionBackground);
    --border: var(--vscode-panel-border);
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: var(--vscode-font-family);
    font-size: var(--vscode-font-size);
    color: var(--vscode-foreground);
    background: var(--vscode-editor-background);
    padding: 16px;
  }
  #toolbar {
    position: sticky; top: 0;
    background: var(--vscode-editor-background);
    padding-bottom: 12px;
    z-index: 10;
    display: flex; gap: 8px; flex-wrap: wrap; align-items: center;
    border-bottom: 1px solid var(--border);
    margin-bottom: 16px;
  }
  #search {
    flex: 1; min-width: 200px;
    background: var(--vscode-input-background);
    color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent);
    padding: 4px 8px; border-radius: 3px;
    font-size: inherit;
  }
  #search:focus { outline: 1px solid var(--vscode-focusBorder); }
  .chip {
    padding: 2px 10px; border-radius: 12px; cursor: pointer; font-size: 0.85em;
    border: 1px solid var(--vscode-button-border, transparent);
    background: var(--vscode-button-secondaryBackground);
    color: var(--vscode-button-secondaryForeground);
    user-select: none;
  }
  .chip.active {
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
  }
  .count { font-size: 0.8em; color: var(--vscode-descriptionForeground); margin-left: auto; }
  .card {
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 12px 14px;
    margin-bottom: 10px;
    background: var(--card-bg);
  }
  .card-header { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
  .cmd-name { font-weight: bold; font-size: 1.05em; font-family: var(--vscode-editor-font-family); }
  .badge {
    font-size: 0.75em; padding: 1px 6px; border-radius: 10px;
    background: var(--vscode-badge-background); color: var(--vscode-badge-foreground);
  }
  .badge.experimental { background: var(--vscode-statusBarItem-warningBackground, #856404); color: #fff; }
  .badge.deprecated   { background: var(--vscode-statusBarItem-errorBackground, #a94442);  color: #fff; }
  .desc { margin-top: 6px; color: var(--vscode-descriptionForeground); }
  .usage {
    margin-top: 8px;
    font-family: var(--vscode-editor-font-family);
    font-size: 0.9em;
    background: var(--vscode-textCodeBlock-background);
    padding: 4px 8px; border-radius: 3px; white-space: pre-wrap;
  }
  .examples { margin-top: 6px; }
  .examples summary { cursor: pointer; font-size: 0.85em; color: var(--vscode-textLink-foreground); }
  .examples pre {
    margin-top: 4px;
    font-family: var(--vscode-editor-font-family); font-size: 0.88em;
    background: var(--vscode-textCodeBlock-background);
    padding: 6px 8px; border-radius: 3px; white-space: pre-wrap; overflow-x: auto;
  }
  .meta { margin-top: 6px; font-size: 0.8em; color: var(--vscode-descriptionForeground); }
  .hidden { display: none; }
</style>
</head>
<body>
<div id="toolbar">
  <input id="search" type="search" placeholder="Search commands…" autocomplete="off">
  <div id="chips"></div>
  <span class="count" id="count"></span>
</div>
<div id="list"></div>
<script>
const ALL = ${entriesJson};
const vscode = acquireVsCodeApi();
let activeCategory = null;

function escHtml(s) {
  return String(s ?? "").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;");
}

function renderCards(entries) {
  const list = document.getElementById("list");
  list.innerHTML = entries.map(e => {
    const badges = [];
    if (e.isExperimental)    badges.push('<span class="badge experimental">⚗ Experimental</span>');
    if (e.deprecatedVersion) badges.push('<span class="badge deprecated">⚠ Deprecated</span>');
    if (e.removedVersion)    badges.push('<span class="badge deprecated">✕ Removed</span>');

    const aliases = e.aliases.length ? '<span class="meta">Aliases: ' + e.aliases.map(a => '<code>' + escHtml(a) + '</code>').join(", ") + '</span>' : '';
    const since = e.sinceVersion ? '<span class="meta">Since ' + escHtml(e.sinceVersion) + '</span>' : '';
    const tags  = e.tags.length  ? '<span class="meta">Tags: ' + e.tags.map(t => escHtml(t)).join(", ") + '</span>' : '';
    const seeAlso = e.seeAlso.length ? '<span class="meta">See also: ' + e.seeAlso.map(s => '<code>' + escHtml(s) + '</code>').join(", ") + '</span>' : '';

    const examplesHtml = e.examples.length ? \`<details class="examples">
      <summary>Examples (\${e.examples.length})</summary>
      <pre>\${escHtml(e.examples.map(x => x.code + (x.title ? "  # " + x.title : "")).join("\\n"))}</pre>
    </details>\` : '';

    return \`<div class="card" id="cmd-\${escHtml(e.name)}" data-name="\${escHtml(e.name)}" data-cat="\${escHtml(e.category)}" data-desc="\${escHtml(e.description)}">
      <div class="card-header">
        <span class="cmd-name">\${escHtml(e.name)}</span>
        \${badges.join("")}
        <span class="badge" style="margin-left:auto">\${escHtml(e.category)}</span>
      </div>
      <div class="desc">\${escHtml(e.description)}</div>
      \${e.usage ? '<div class="usage">' + escHtml(e.usage) + '</div>' : ''}
      \${examplesHtml}
      \${[aliases, since, tags, seeAlso].filter(Boolean).join(" &nbsp; ")}
    </div>\`;
  }).join("");
  document.getElementById("count").textContent = entries.length + " commands";
}

function buildChips() {
  const cats = [...new Set(ALL.map(e => e.category))].sort();
  const chips = document.getElementById("chips");
  chips.innerHTML = cats.map(cat =>
    \`<span class="chip" data-cat="\${escHtml(cat)}">\${escHtml(cat)}</span>\`
  ).join("");
  chips.querySelectorAll(".chip").forEach(chip => {
    chip.addEventListener("click", () => {
      if (activeCategory === chip.dataset.cat) {
        activeCategory = null;
        chip.classList.remove("active");
      } else {
        chips.querySelectorAll(".chip").forEach(c => c.classList.remove("active"));
        activeCategory = chip.dataset.cat;
        chip.classList.add("active");
      }
      filter();
    });
  });
}

function filter() {
  const q = document.getElementById("search").value.toLowerCase();
  const visible = ALL.filter(e => {
    const matchCat = !activeCategory || e.category === activeCategory;
    const matchQ   = !q || e.name.toLowerCase().includes(q) || e.description.toLowerCase().includes(q) || e.aliases.some(a => a.toLowerCase().includes(q));
    return matchCat && matchQ;
  });
  renderCards(visible);
}

document.getElementById("search").addEventListener("input", filter);

window.addEventListener("message", ev => {
  if (ev.data.type === "scrollTo") {
    const el = document.getElementById("cmd-" + ev.data.name);
    if (el) el.scrollIntoView({ behavior: "smooth", block: "start" });
  }
});

buildChips();
filter();
</script>
</body>
</html>`;
    }
}

// ─── CodeLens ─────────────────────────────────────────────────────────────────

class ToshCodeLensProvider {
    provideCodeLenses(document) {
        const lenses = [];
        const topRange = new vscode.Range(0, 0, 0, 0);

        lenses.push(new vscode.CodeLens(topRange, {
            title: "▶ Run",
            command: "tosh.runFile",
            tooltip: "Run this script in a TōSh terminal"
        }));
        lenses.push(new vscode.CodeLens(topRange, {
            title: "$(debug-alt) Debug",
            command: "tosh.debugFile",
            tooltip: "Debug this script with the TōSh DAP server"
        }));
        lenses.push(new vscode.CodeLens(topRange, {
            title: "▶ Run with Args...",
            command: "tosh.runFileWithArgs",
            tooltip: "Prompt for arguments, then run"
        }));

        return lenses;
    }
}

// ─── Folding ──────────────────────────────────────────────────────────────────

const TRIPLE_DELIMITERS = ['"""', "'''"];

const FOLD_BRACKETS = [
    { open: "{|", close: "|}" },
    { open: "{%", close: "%}" },
    { open: "{:", close: ":}" },
    { open: "{", close: "}" },
    { open: "[", close: "]" },
    { open: "(", close: ")" }
];

/**
 * True when the `#` at `index` opens a comment of any form.
 *
 * Mirrors `ToshCommentSyntax` in the runtime: `##` is unconditional, while a
 * lone `#` only opens a comment when it stands alone as a word — at the start
 * of a word and followed by whitespace or the end of the line. That is what
 * keeps `#ff0000`, `issue#42` and `C#` out of comment scope.
 */
function opensToshComment(text, index) {
    if (text[index] !== "#") return false;
    if (text[index + 1] === "#") return true;

    const previous = index > 0 ? text[index - 1] : "";
    const next = index + 1 < text.length ? text[index + 1] : "";
    const atWordStart = index === 0 || /\s/.test(previous);
    const terminated = next === "" || /\s/.test(next);
    return atWordStart && terminated;
}

/**
 * Lexes one line, carrying multi-line string and block-comment state across
 * calls in `state`. Returns the line's *code* with strings and comments blanked
 * to spaces, so bracket counting can never be fooled by a `{` inside a string
 * or a comment, plus the classification the folder needs.
 */
function scanToshLine(text, state) {
    const result = {
        code: "",
        commentKind: null,      // "doc" | "line" — a comment this line opens
        blockCommentOpened: false,
        blockCommentClosed: false,
        isBlank: text.trim().length === 0
    };

    const blank = (count) => " ".repeat(count);
    let i = 0;
    const n = text.length;

    while (i < n) {
        if (state.inBlockComment) {
            const close = text.indexOf("}##", i);
            if (close === -1) { result.code += blank(n - i); break; }
            state.inBlockComment = false;
            result.blockCommentClosed = true;
            result.code += blank(close + 3 - i);
            i = close + 3;
            continue;
        }

        if (state.tripleDelim) {
            const close = text.indexOf(state.tripleDelim, i);
            if (close === -1) { result.code += blank(n - i); break; }
            const width = state.tripleDelim.length;
            state.tripleDelim = null;
            result.code += blank(close + width - i);
            i = close + width;
            continue;
        }

        const ch = text[i];
        const isDollar = ch === "$";
        const quoteAt = isDollar ? i + 1 : i;
        const triple = text.substr(quoteAt, 3);

        // Triple-quoted forms: """…""", '''…''', $"""…""", $'''…'''
        if (TRIPLE_DELIMITERS.includes(triple)) {
            const bodyStart = quoteAt + 3;
            const close = text.indexOf(triple, bodyStart);
            if (close === -1) {
                state.tripleDelim = triple;
                result.code += blank(n - i);
                break;
            }
            result.code += blank(close + 3 - i);
            i = close + 3;
            continue;
        }

        // Single-line string forms: "…", '…', $"…", $'…'
        const quoteChar = text[quoteAt];
        if (quoteChar === '"' || quoteChar === "'") {
            let j = quoteAt + 1;
            // Single-quoted strings are literal — no escape processing.
            const honoursEscapes = quoteChar === '"';
            while (j < n) {
                if (honoursEscapes && text[j] === "\\") { j += 2; continue; }
                if (text[j] === quoteChar) { j++; break; }
                j++;
            }
            result.code += blank(Math.min(j, n) - i);
            i = j;
            continue;
        }

        if (ch === "#") {
            // `##{` opens a block comment; everything else is a line comment.
            if (text.startsWith("##{", i)) {
                const close = text.indexOf("}##", i + 3);
                if (close === -1) {
                    state.inBlockComment = true;
                    result.blockCommentOpened = true;
                    result.code += blank(n - i);
                    break;
                }
                // Opened and closed on this same line — no folding state changes.
                result.code += blank(close + 3 - i);
                i = close + 3;
                continue;
            }

            if (opensToshComment(text, i)) {
                result.commentKind = text[i + 1] === "#" ? "doc" : "line";
                result.code += blank(n - i);
                break;
            }
        }

        result.code += ch;
        i++;
    }

    return result;
}

/**
 * Folding for TōSh. The language server does not implement
 * `textDocument/foldingRange`, so this is the only structural source; VS Code
 * merges it with the marker rules in language-configuration.json.
 *
 * Provides:
 *   - doc-comment blocks (`##`) above declarations, as Comment ranges so
 *     "Fold All Block Comments" reaches them
 *   - `##{ … }##` block comments
 *   - runs of ordinary `#` comments
 *   - `# region` / `# endregion` pairs, as Region ranges
 *   - every bracket pair, including the `{| |}` `{% %}` `{: :}` collection
 *     literals, counted against string- and comment-masked text
 */
class ToshFoldingRangeProvider {
    provideFoldingRanges(document, _context, cancellation) {
        const ranges = [];
        const state = { inBlockComment: false, tripleDelim: null };

        const bracketStack = [];
        const regionStack = [];

        let commentRunStart = -1;
        let commentRunKind = null;
        let blockCommentStart = -1;
        let tripleStringStart = -1;

        const flushCommentRun = (endLine) => {
            if (commentRunStart >= 0 && endLine > commentRunStart) {
                ranges.push(new vscode.FoldingRange(
                    commentRunStart, endLine, vscode.FoldingRangeKind.Comment));
            }
            commentRunStart = -1;
            commentRunKind = null;
        };

        for (let line = 0; line < document.lineCount; line++) {
            if (cancellation && cancellation.isCancellationRequested) return ranges;

            const text = document.lineAt(line).text;
            const wasInBlockComment = state.inBlockComment;
            const wasInTripleString = state.tripleDelim !== null;
            const scan = scanToshLine(text, state);

            // ── Block comments ──
            if (!wasInBlockComment && state.inBlockComment) {
                blockCommentStart = line;
            } else if (wasInBlockComment && !state.inBlockComment) {
                if (blockCommentStart >= 0 && line > blockCommentStart) {
                    ranges.push(new vscode.FoldingRange(
                        blockCommentStart, line, vscode.FoldingRangeKind.Comment));
                }
                blockCommentStart = -1;
            }

            // ── Multi-line strings ──
            if (!wasInTripleString && state.tripleDelim) {
                tripleStringStart = line;
            } else if (wasInTripleString && !state.tripleDelim) {
                if (tripleStringStart >= 0 && line > tripleStringStart) {
                    ranges.push(new vscode.FoldingRange(tripleStringStart, line));
                }
                tripleStringStart = -1;
            }

            if (state.inBlockComment || state.tripleDelim || wasInBlockComment || wasInTripleString) {
                continue;
            }

            // ── Region markers ──
            const regionOpen = /^\s*#\s*region\b/.test(text);
            const regionClose = /^\s*#\s*endregion\b/.test(text);
            if (regionOpen) {
                flushCommentRun(line - 1);
                regionStack.push(line);
                continue;
            }
            if (regionClose) {
                flushCommentRun(line - 1);
                const start = regionStack.pop();
                if (start !== undefined && line > start) {
                    ranges.push(new vscode.FoldingRange(
                        start, line, vscode.FoldingRangeKind.Region));
                }
                continue;
            }

            // ── Comment runs ──
            // A run is a maximal block of consecutive whole-line comments of the
            // same kind. Doc and plain comments never merge into one range, so a
            // `##` block above a declaration folds on its own.
            const isWholeLineComment =
                scan.commentKind !== null && scan.code.trim().length === 0;

            if (isWholeLineComment) {
                if (commentRunKind !== scan.commentKind) {
                    flushCommentRun(line - 1);
                    commentRunStart = line;
                    commentRunKind = scan.commentKind;
                }
            } else {
                flushCommentRun(line - 1);
            }

            // ── Brackets ──
            const code = scan.code;
            for (let i = 0; i < code.length; i++) {
                const two = code.substr(i, 2);
                const opener = FOLD_BRACKETS.find(b => b.open.length === 2 && b.open === two)
                    || FOLD_BRACKETS.find(b => b.open.length === 1 && b.open === code[i]);
                const closer = FOLD_BRACKETS.find(b => b.close.length === 2 && b.close === two)
                    || FOLD_BRACKETS.find(b => b.close.length === 1 && b.close === code[i]);

                // A two-character collection delimiter wins over the bare brace.
                if (closer && closer.close.length === 2) {
                    popBracket(bracketStack, ranges, closer.close, line);
                    i++;
                    continue;
                }
                if (opener && opener.open.length === 2) {
                    bracketStack.push({ close: opener.close, line });
                    i++;
                    continue;
                }
                if (closer) {
                    popBracket(bracketStack, ranges, closer.close, line);
                    continue;
                }
                if (opener) {
                    bracketStack.push({ close: opener.close, line });
                }
            }
        }

        flushCommentRun(document.lineCount - 1);
        if (blockCommentStart >= 0 && document.lineCount - 1 > blockCommentStart) {
            ranges.push(new vscode.FoldingRange(
                blockCommentStart, document.lineCount - 1, vscode.FoldingRangeKind.Comment));
        }

        return ranges;
    }
}

function popBracket(stack, ranges, closeToken, line) {
    // Tolerate mismatches: unwind to the nearest matching opener so one stray
    // bracket cannot destroy folding for the rest of the file.
    for (let i = stack.length - 1; i >= 0; i--) {
        if (stack[i].close !== closeToken) continue;
        const entry = stack[i];
        stack.length = i;
        if (line > entry.line) {
            ranges.push(new vscode.FoldingRange(entry.line, line - 1));
        }
        return;
    }
}

// ─── Local language providers (fallback when LSP unavailable) ─────────────────

class ToshCompletionProvider {
    provideCompletionItems(document, position) {
        const linePrefix = document.lineAt(position.line).text.slice(0, position.character);
        const completions = [];

        if (linePrefix.endsWith("$")) {
            completions.push(...buildVariableCompletions(document));
            completions.push(...buildSpecialVariableCompletions());
            return completions;
        }

        completions.push(...buildKeywordCompletions());
        completions.push(...buildBuiltinCompletions());
        completions.push(...buildDeclaredSymbolCompletions(document));
        completions.push(...buildSpecialVariableCompletions());

        return completions;
    }
}

class ToshHoverProvider {
    provideHover(document, position) {
        const range = document.getWordRangeAtPosition(position, /[$A-Za-z_][A-Za-z0-9_.-]*/);
        if (!range) {
            return null;
        }

        const word = document.getText(range);

        // Try rich metadata first
        if (richMetadataMap && richMetadataMap.has(word)) {
            const entry = richMetadataMap.get(word);
            return new vscode.Hover(new vscode.MarkdownString(formatRichHover(entry)), range);
        }

        const description =
            languageData.specialVariables[word] ||
            languageData.keywords[word] ||
            languageData.builtins[word];

        if (!description) {
            return null;
        }

        return new vscode.Hover(new vscode.MarkdownString(`**${word}**\n\n${description}`), range);
    }
}

function formatRichHover(entry) {
    const parts = [];

    // Badges
    const badges = [];
    if (entry.isExperimental) badges.push("⚗️ Experimental");
    if (entry.deprecatedVersion) badges.push(`⚠️ Deprecated since ${entry.deprecatedVersion}`);
    if (entry.removedVersion) badges.push(`❌ Removed in ${entry.removedVersion}`);
    if (badges.length > 0) parts.push(badges.join(" · "));

    parts.push(`**${entry.name}**`);
    parts.push(entry.description);

    if (entry.longDescription) {
        parts.push(entry.longDescription);
    }

    if (entry.aliases && entry.aliases.length > 0) {
        parts.push(`*Aliases:* ${entry.aliases.map(a => "\`" + a + "\`").join(", ")}`);
    }

    // Category and version
    const info = [`Category: ${entry.category}`];
    if (entry.sinceVersion) info.push(`Since: ${entry.sinceVersion}`);
    parts.push(`*${info.join(" · ")}*`);

    parts.push("```tosh\n" + entry.usage + "\n```");

    if (entry.arguments && entry.arguments.length > 0) {
        parts.push("**Arguments**");
        for (const arg of entry.arguments) {
            const req = arg.required ? "" : " *(optional)*";
            const typePart = arg.typeName ? ` \`${arg.typeName}\`` : "";
            parts.push(`- \`${arg.name}\`${typePart} — ${arg.description}${req}`);
        }
    }

    if (entry.options && entry.options.length > 0) {
        parts.push("**Options**");
        for (const opt of entry.options) {
            parts.push(`- \`${opt.syntax}\` — ${opt.description}`);
        }
    }

    if (entry.pipelineInput) {
        const accepts = [];
        if (entry.pipelineInput.acceptsScalar) accepts.push("scalar");
        if (entry.pipelineInput.acceptsRecord) accepts.push("record");
        if (entry.pipelineInput.acceptsList) accepts.push("list");
        if (entry.pipelineInput.acceptsTable) accepts.push("table");
        if (accepts.length > 0) {
            let line = `**Pipeline input:** ${accepts.join(", ")}`;
            if (entry.pipelineInput.description) line += `\n  ${entry.pipelineInput.description}`;
            parts.push(line);
        }
    }

    if (entry.output) {
        parts.push(`**Output:** ${entry.output}`);
    }

    if (entry.examples && entry.examples.length > 0) {
        const exLines = entry.examples.map(ex => {
            const comment = ex.title ? `  # ${ex.title}` : "";
            return `${ex.code}${comment}`;
        });
        parts.push("**Examples**\n```tosh\n" + exLines.join("\n") + "\n```");
    }

    if (entry.canonicalExamples && entry.canonicalExamples.length > 0) {
        parts.push("**Canonical Examples**");
        for (const ce of entry.canonicalExamples) {
            if (ce.description) parts.push(`*${ce.description}*`);
            parts.push("```tosh\n> " + ce.input + "\n" + ce.output + "\n```");
        }
    }

    if (entry.notes && entry.notes.length > 0) {
        for (const note of entry.notes) {
            parts.push(`> ${note}`);
        }
    }

    if (entry.errorConditions && entry.errorConditions.length > 0) {
        parts.push("**Error Conditions**");
        for (const err of entry.errorConditions) {
            parts.push(`- ${err}`);
        }
    }

    if (entry.permissions && entry.permissions.length > 0) {
        parts.push(`**Permissions:** ${entry.permissions.join(", ")}`);
    }

    if (entry.tags && entry.tags.length > 0) {
        parts.push(`*Tags:* ${entry.tags.map(t => "\`" + t + "\`").join(", ")}`);
    }

    if (entry.seeAlso && entry.seeAlso.length > 0) {
        parts.push(`*See also:* ${entry.seeAlso.map(s => "\`" + s + "\`").join(", ")}`);
    }

    return parts.join("\n\n");
}

// Every declaration form the language has, so the outline and the fallback
// completions stay in step with the grammar. MODIFIER_PREFIX matches any run of
// leading modifiers, since `shy static overrule func f` is one declaration.
const MODIFIER_PREFIX =
    "(?:(?:shy|private|proud|public|guarded|protected|local|global|export|sealed|hollow|" +
    "abstract|partial|overrule|override|static|shared|hermit|strict|fluid|leaky|lazy|" +
    "fixed|readonly|vital|required|fading|obsolete|raw|eager|hidden)\\s+)*";

const DECLARATION_FORMS = [
    { keyword: "class", symbol: "Class", completion: "Class" },
    { keyword: "interface", symbol: "Interface", completion: "Interface" },
    { keyword: "struct", symbol: "Struct", completion: "Struct" },
    { keyword: "trait", symbol: "Interface", completion: "Interface" },
    { keyword: "union", symbol: "Enum", completion: "Enum" },
    { keyword: "record", symbol: "Struct", completion: "Struct" },
    { keyword: "enum", symbol: "Enum", completion: "Enum" },
    { keyword: "module", symbol: "Module", completion: "Module" },
    { keyword: "type", symbol: "Struct", completion: "Struct" },
    { keyword: "rune", symbol: "Function", completion: "Function" },
    { keyword: "event", symbol: "Event", completion: "Event" },
    { keyword: "subcommand", symbol: "Function", completion: "Function" },
    { keyword: "func", symbol: "Function", completion: "Function" },
    { keyword: "prop", symbol: "Property", completion: "Property" }
];

function declarationRegex(keyword, flags) {
    return new RegExp(
        `^\\s*${MODIFIER_PREFIX}${keyword}\\s+([A-Za-z_][A-Za-z0-9_-]*)`, flags);
}

class ToshDocumentSymbolProvider {
    provideDocumentSymbols(document) {
        const topSymbols = [];
        const containerStack = [];
        const text = document.getText();
        const lines = text.split(/\r?\n/);

        const patterns = DECLARATION_FORMS.map(form => ({
            keyword: form.keyword,
            regex: declarationRegex(form.keyword),
            kind: vscode.SymbolKind[form.symbol]
        }));

        lines.forEach((line, index) => {
            const trimmed = line.trim();
            if (!trimmed || trimmed.startsWith("#")) return;

            for (const pattern of patterns) {
                const match = line.match(pattern.regex);
                if (!match) continue;

                const name = match[1];
                const start = new vscode.Position(index, match.index || 0);
                const end = new vscode.Position(index, line.length);
                const range = new vscode.Range(start, end);

                const sym = new vscode.DocumentSymbol(
                    name,
                    pattern.keyword,
                    pattern.kind,
                    range,
                    range
                );

                const indent = line.search(/\S/);
                while (containerStack.length > 0 && containerStack[containerStack.length - 1].indent >= indent) {
                    containerStack.pop();
                }

                if (containerStack.length > 0) {
                    containerStack[containerStack.length - 1].symbol.children.push(sym);
                } else {
                    topSymbols.push(sym);
                }

                if (["module", "class", "record", "enum", "subcommand"].includes(pattern.keyword)) {
                    containerStack.push({ indent, symbol: sym });
                }

                break;
            }
        });

        return topSymbols;
    }
}

function buildKeywordCompletions() {
    return Object.entries(languageData.keywords).map(([label, description]) => {
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Keyword);
        item.detail = "TōSh keyword";
        item.documentation = description;
        return item;
    });
}

function buildBuiltinCompletions() {
    if (richMetadata) {
        return richMetadata.map(entry => {
            const item = new vscode.CompletionItem(entry.name, vscode.CompletionItemKind.Function);
            item.detail = `Built-in (${entry.category})`;
            item.documentation = new vscode.MarkdownString(
                (entry.longDescription || entry.description) +
                (entry.usage ? "\n\n```tosh\n" + entry.usage + "\n```" : "")
            );
            if (entry.deprecatedVersion) {
                item.tags = [vscode.CompletionItemTag.Deprecated];
            }
            return item;
        });
    }

    return Object.entries(languageData.builtins).map(([label, description]) => {
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Function);
        item.detail = "TōSh built-in";
        item.documentation = description;
        return item;
    });
}

function buildSpecialVariableCompletions() {
    return Object.entries(languageData.specialVariables).map(([label, description]) => {
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Variable);
        item.detail = "TōSh special variable";
        item.documentation = description;
        return item;
    });
}

function buildDeclaredSymbolCompletions(document) {
    const completions = [];
    const seen = new Set();
    const patterns = DECLARATION_FORMS.map(form => ({
        regex: declarationRegex(form.keyword, "gm"),
        kind: vscode.CompletionItemKind[form.completion]
    }));

    for (const pattern of patterns) {
        let match;
        while ((match = pattern.regex.exec(document.getText())) !== null) {
            const name = match[1];
            if (seen.has(name)) {
                continue;
            }

            seen.add(name);
            const item = new vscode.CompletionItem(name, pattern.kind);
            item.detail = "Declared in current document";
            completions.push(item);
        }
    }

    return completions;
}

function buildVariableCompletions(document) {
    const completions = [];
    const seen = new Set();
    // `const` and `prop` bind names referenced as `$name` too, not just `var`.
    const text = document.getText();

    for (const keyword of ["var", "const", "prop", "alloc"]) {
        const regex = declarationRegex(keyword, "gm");
        let match;
        while ((match = regex.exec(text)) !== null) {
            const label = `$${match[1]}`;
            if (seen.has(label)) {
                continue;
            }

            seen.add(label);
            const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Variable);
            item.detail = `Declared with '${keyword}' in current document`;
            completions.push(item);
        }
    }

    return completions;
}

// ─── Install check ────────────────────────────────────────────────────────────

function checkToshInstall() {
    const config = vscode.workspace.getConfiguration("tosh");
    const shellPath = config.get("terminal.path", "tosh");
    const result = childProcess.spawnSync(shellPath, ["--version"], {
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        timeout: 5000
    });
    if (result.status === 0) {
        const ver = (result.stdout || Buffer.alloc(0)).toString("utf8").trim() ||
            (result.stderr || Buffer.alloc(0)).toString("utf8").trim();
        vscode.window.showInformationMessage(`TōSh is installed: ${ver || "(version unavailable)"}`);
    } else {
        vscode.window.showErrorMessage(
            `TōSh not found at "${shellPath}". Install TōSh or update the tosh.terminal.path setting.`,
            "Open Settings"
        ).then(choice => {
            if (choice === "Open Settings")
                vscode.commands.executeCommand("workbench.action.openSettings", "tosh.terminal.path");
        });
    }
}

// ─── REPL Panel ───────────────────────────────────────────────────────────────

class ToshReplPanel {
    static _current = null;

    static show(context) {
        if (ToshReplPanel._current) {
            ToshReplPanel._current._panel.reveal(vscode.ViewColumn.Two);
            return;
        }
        const panel = vscode.window.createWebviewPanel(
            "tosh.repl",
            "TōSh REPL",
            vscode.ViewColumn.Two,
            { enableScripts: true, retainContextWhenHidden: true }
        );
        ToshReplPanel._current = new ToshReplPanel(panel, context);
    }

    constructor(panel, context) {
        this._panel = panel;
        this._context = context;
        this._proc = null;

        this._panel.webview.html = this._buildHtml();
        this._startProcess();

        this._panel.webview.onDidReceiveMessage(msg => {
            switch (msg.type) {
                case "run": this._run(msg.code); break;
                case "clear": this._post({ type: "clear" }); break;
                case "restart": this._restart(); break;
                case "ready": this._post({ type: "status", state: this._proc ? "running" : "stopped" }); break;
            }
        });

        this._panel.onDidDispose(() => {
            this._kill();
            ToshReplPanel._current = null;
        });
    }

    _startProcess() {
        const config = vscode.workspace.getConfiguration("tosh");
        const shellPath = config.get("terminal.path", "tosh");
        try {
            this._proc = childProcess.spawn(shellPath, ["--no-profile"], {
                stdio: ["pipe", "pipe", "pipe"],
                env: { ...process.env }
            });
        } catch (err) {
            this._post({ type: "output", text: `[Failed to start TōSh: ${err.message}]\n`, stream: "system" });
            this._post({ type: "status", state: "stopped" });
            return;
        }

        this._post({ type: "status", state: "running" });

        let stdoutBuf = "";
        this._proc.stdout.on("data", chunk => {
            stdoutBuf += chunk.toString("utf8");
            const lines = stdoutBuf.split("\n");
            stdoutBuf = lines.pop();
            for (const line of lines)
                this._post({ type: "output", text: line + "\n", stream: "stdout" });
        });
        this._proc.stdout.on("close", () => {
            if (stdoutBuf) { this._post({ type: "output", text: stdoutBuf, stream: "stdout" }); stdoutBuf = ""; }
        });
        this._proc.stderr.on("data", chunk => {
            this._post({ type: "output", text: chunk.toString("utf8"), stream: "stderr" });
        });
        this._proc.on("exit", (code, signal) => {
            const detail = code != null ? `code ${code}` : signal ? `signal ${signal}` : "unknown";
            this._post({ type: "output", text: `\n[Process exited — ${detail}]\n`, stream: "system" });
            this._post({ type: "status", state: "stopped" });
            this._proc = null;
        });
        this._proc.on("error", err => {
            this._post({ type: "output", text: `\n[Error: ${err.message}]\n`, stream: "system" });
            this._post({ type: "status", state: "stopped" });
            this._proc = null;
        });
    }

    _run(code) {
        if (!code.trim()) return;
        if (!this._proc) {
            this._startProcess();
            setTimeout(() => this._sendLine(code), 150);
            return;
        }
        this._sendLine(code);
    }

    _sendLine(code) {
        this._post({ type: "echo", text: code });
        try { this._proc.stdin.write(code + "\n"); }
        catch (err) { this._post({ type: "output", text: `[Write error: ${err.message}]\n`, stream: "system" }); }
    }

    _restart() {
        this._kill();
        this._post({ type: "output", text: "\n[Restarting TōSh...]\n", stream: "system" });
        this._startProcess();
    }

    _kill() {
        if (this._proc) { try { this._proc.kill(); } catch { } this._proc = null; }
    }

    _post(msg) {
        try { this._panel.webview.postMessage(msg); } catch { }
    }

    _buildHtml() {
        return /* html */`<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>TōSh REPL</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { height: 100%; }
  body {
    display: flex; flex-direction: column; height: 100%;
    font-family: var(--vscode-editor-font-family, monospace);
    font-size: var(--vscode-editor-font-size, 13px);
    color: var(--vscode-terminal-foreground, var(--vscode-foreground));
    background: var(--vscode-terminal-background, var(--vscode-editor-background));
  }
  #toolbar {
    flex: 0 0 auto; display: flex; gap: 6px; align-items: center;
    padding: 4px 8px;
    background: var(--vscode-editorGroupHeader-tabsBackground, var(--vscode-editor-background));
    border-bottom: 1px solid var(--vscode-panel-border);
  }
  #dot {
    width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0;
    background: var(--vscode-terminal-ansiGreen, #4ec9b0); transition: background 0.2s;
  }
  #dot.stopped { background: var(--vscode-errorForeground, #f44747); }
  #lbl { font-size: 0.8em; color: var(--vscode-descriptionForeground); flex: 1; }
  button {
    background: var(--vscode-button-secondaryBackground);
    color: var(--vscode-button-secondaryForeground);
    border: 1px solid var(--vscode-button-border, transparent);
    padding: 2px 10px; border-radius: 2px; cursor: pointer; font-size: 0.8em;
  }
  button:hover { background: var(--vscode-button-secondaryHoverBackground); }
  #out {
    flex: 1 1 auto; overflow-y: auto;
    padding: 8px 10px; white-space: pre-wrap; word-break: break-all; line-height: 1.4;
  }
  .ln-echo   { color: var(--vscode-terminal-ansiCyan, #4fc1ff); }
  .ln-echo::before { content: "> "; opacity: 0.5; }
  .ln-err    { color: var(--vscode-terminal-ansiRed, #f44747); }
  .ln-sys    { color: var(--vscode-descriptionForeground); font-style: italic; }
  #ia {
    flex: 0 0 auto; display: flex; align-items: flex-start;
    border-top: 1px solid var(--vscode-panel-border);
    padding: 4px 8px; gap: 6px;
  }
  #prompt {
    color: var(--vscode-terminal-ansiGreen, #4ec9b0);
    font-weight: bold; flex-shrink: 0; padding-top: 5px; user-select: none;
  }
  #inp {
    flex: 1; background: transparent; color: inherit;
    border: none; outline: none;
    font-family: inherit; font-size: inherit; line-height: 1.4;
    resize: none; min-height: 24px; max-height: 120px; overflow-y: auto; padding: 4px 0;
  }
  #run-btn {
    flex-shrink: 0;
    background: var(--vscode-button-background); color: var(--vscode-button-foreground);
  }
  #run-btn:hover { background: var(--vscode-button-hoverBackground); }
</style>
</head>
<body>
<div id="toolbar">
  <div id="dot"></div>
  <span id="lbl">Starting...</span>
  <button id="clr">Clear</button>
  <button id="rst">Restart</button>
</div>
<div id="out"></div>
<div id="ia">
  <span id="prompt">&gt;</span>
  <textarea id="inp" rows="1" placeholder="Enter TōSh expression..." autocomplete="off" spellcheck="false"></textarea>
  <button id="run-btn">Run</button>
</div>
<script>
const vscode = acquireVsCodeApi();
const outEl = document.getElementById('out');
const inEl  = document.getElementById('inp');
const dotEl = document.getElementById('dot');
const lblEl = document.getElementById('lbl');
const hist = []; let histIdx = -1, saved = '';

const ESC = String.fromCharCode(27);
const COLORS = {
  30:'#808080',31:'#cd3131',32:'#0dbc79',33:'#e5e510',
  34:'#2472c8',35:'#bc3fbc',36:'#11a8cd',37:'#e5e5e5',
  90:'#666',91:'#f14c4c',92:'#23d18b',93:'#f5f543',
  94:'#3b8eea',95:'#d670d6',96:'#29b8db',97:'#e5e5e5'
};

function esc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

function ansi(text) {
  const parts = text.split(ESC);
  let html = esc(parts[0]), bold = false, color = '';
  for (let i = 1; i < parts.length; i++) {
    if (parts[i].charAt(0) === '[') {
      const end = parts[i].indexOf('m');
      if (end !== -1) {
        for (const c of parts[i].slice(1, end).split(';').map(Number)) {
          if (c === 0) { bold = false; color = ''; }
          else if (c === 1) bold = true;
          else if (COLORS[c]) color = COLORS[c];
        }
        const rest = parts[i].slice(end + 1);
        if (rest) {
          const st = [bold?'font-weight:bold':'', color?'color:'+color:''].filter(Boolean).join(';');
          html += st ? '<span style="'+st+'">'+esc(rest)+'</span>' : esc(rest);
        }
        continue;
      }
    }
    html += esc(parts[i]);
  }
  return html;
}

function append(text, cls) {
  const d = document.createElement('div');
  d.className = cls || '';
  if (cls === 'ln-echo' || cls === 'ln-sys') d.textContent = text;
  else d.innerHTML = ansi(text);
  outEl.appendChild(d);
  outEl.scrollTop = outEl.scrollHeight;
}

window.addEventListener('message', ev => {
  const m = ev.data;
  switch (m.type) {
    case 'output': append(m.text, m.stream === 'stderr' ? 'ln-err' : m.stream === 'system' ? 'ln-sys' : ''); break;
    case 'echo':   append(m.text, 'ln-echo'); break;
    case 'clear':  outEl.innerHTML = ''; break;
    case 'status':
      const run = m.state === 'running';
      dotEl.className = run ? '' : 'stopped';
      lblEl.textContent = run ? 'Running' : 'Stopped';
      break;
  }
});

function submit() {
  const code = inEl.value;
  if (!code.trim()) return;
  hist.unshift(code); histIdx = -1; saved = '';
  inEl.value = ''; resize();
  vscode.postMessage({ type: 'run', code });
}

document.getElementById('run-btn').onclick = submit;
document.getElementById('clr').onclick = () => vscode.postMessage({ type: 'clear' });
document.getElementById('rst').onclick = () => vscode.postMessage({ type: 'restart' });

inEl.addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); return; }
  if (e.key === 'ArrowUp' && inEl.selectionStart === 0) {
    e.preventDefault();
    if (histIdx === -1) saved = inEl.value;
    if (histIdx < hist.length - 1) { histIdx++; inEl.value = hist[histIdx]; setCaret(); resize(); }
  } else if (e.key === 'ArrowDown') {
    e.preventDefault();
    if (histIdx > 0)      { histIdx--; inEl.value = hist[histIdx]; }
    else if (histIdx === 0) { histIdx = -1; inEl.value = saved; }
    setCaret(); resize();
  }
});

function setCaret() { inEl.selectionStart = inEl.selectionEnd = inEl.value.length; }
function resize() { inEl.style.height = 'auto'; inEl.style.height = Math.min(inEl.scrollHeight, 120) + 'px'; }
inEl.addEventListener('input', resize);

vscode.postMessage({ type: 'ready' });
inEl.focus();
</script>
</body>
</html>`;
    }
}

// ─── Task provider ────────────────────────────────────────────────────────────

class ToshTaskProvider {
    provideTasks() {
        const folders = vscode.workspace.workspaceFolders || [];
        const tasks = [];
        for (const folder of folders) {
            tasks.push(new vscode.Task(
                { type: "tosh", task: "build-solution" },
                folder,
                "Build Solution",
                "TōSh",
                new vscode.ShellExecution("dotnet build"),
                "$msCompile"
            ));
            tasks.push(new vscode.Task(
                { type: "tosh", task: "run-file" },
                folder,
                "Run File",
                "TōSh",
                new vscode.ShellExecution("tosh \"${file}\"")
            ));
        }
        return tasks;
    }

    resolveTask(task) {
        return task;
    }
}

module.exports = {
    activate,
    deactivate
};
