"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const languageData = require("./language-data.json");

let client = null;

async function activate(context) {
    const selector = { language: "tosh", scheme: "file" };
    const outputChannel = vscode.window.createOutputChannel("ToSh");
    context.subscriptions.push(outputChannel);

    const started = await tryStartLanguageClient(context, selector, outputChannel);
    if (!started) {
        outputChannel.appendLine("Using built-in editor providers because the ToSh language server is unavailable.");
        registerLocalProviders(context, selector);
    }
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
        outputChannel.appendLine("ToSh language server is disabled in settings.");
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
        "ToSh Language Server",
        serverOptions,
        {
            documentSelector: [selector],
            outputChannel,
            synchronize: {
                fileEvents: vscode.workspace.createFileSystemWatcher("**/*.tosh")
            }
        }
    );

    try {
        context.subscriptions.push(languageClient.start());
        client = languageClient;
        outputChannel.appendLine(`Started ToSh language server with: ${describeServerOptions(serverOptions.run)}`);
        return true;
    } catch (error) {
        outputChannel.appendLine(`Failed to start the ToSh language server; falling back to local providers. ${formatError(error)}`);
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
            outputChannel.appendLine(`Configured ToSh language server path was not found: ${configuredServerPath}`);
            return null;
        }

        return createServerOptions(dotnetPath, explicitPath);
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
    const args = projectMode
        ? ["run", "--project", normalizedPath, "--", "--stdio"]
        : [normalizedPath, "--stdio"];
    const commandOptions = {
        command: dotnetPath,
        args,
        options: {
            cwd: projectMode ? path.dirname(normalizedPath) : path.dirname(normalizedPath)
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

class ToshDocumentSymbolProvider {
    provideDocumentSymbols(document) {
        const symbols = [];
        const text = document.getText();
        const lines = text.split(/\r?\n/);

        const patterns = [
            {
                regex: /^\s*(?:(?:shy|global|export)\s+)?class\s+([A-Za-z_][A-Za-z0-9_-]*)/,
                kind: vscode.SymbolKind.Class
            },
            {
                regex: /^\s*(?:(?:shy|global|export)\s+)?module\s+([A-Za-z_][A-Za-z0-9_-]*)/,
                kind: vscode.SymbolKind.Module
            },
            {
                regex: /^\s*(?:(?:shy|global|export)\s+)?enum\s+([A-Za-z_][A-Za-z0-9_-]*)/,
                kind: vscode.SymbolKind.Enum
            },
            {
                regex: /^\s*(?:(?:shy|global|export)\s+)?record\s+([A-Za-z_][A-Za-z0-9_-]*)/,
                kind: vscode.SymbolKind.Struct
            },
            {
                regex: /^\s*(?:(?:shy|global|export)\s+)?func\s+([A-Za-z_][A-Za-z0-9_-]*)/,
                kind: vscode.SymbolKind.Function
            },
            {
                regex: /^\s*(?:(?:shy|global|export)\s+)?var\s+([A-Za-z_][A-Za-z0-9_]*)/,
                kind: vscode.SymbolKind.Variable
            }
        ];

        lines.forEach((line, index) => {
            for (const pattern of patterns) {
                const match = line.match(pattern.regex);
                if (!match) {
                    continue;
                }

                const start = new vscode.Position(index, match.index || 0);
                const end = new vscode.Position(index, line.length);
                symbols.push(new vscode.DocumentSymbol(
                    match[1],
                    "",
                    pattern.kind,
                    new vscode.Range(start, end),
                    new vscode.Range(start, end)
                ));
                break;
            }
        });

        return symbols;
    }
}

function buildKeywordCompletions() {
    return Object.entries(languageData.keywords).map(([label, description]) => {
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Keyword);
        item.detail = "ToSh keyword";
        item.documentation = description;
        return item;
    });
}

function buildBuiltinCompletions() {
    return Object.entries(languageData.builtins).map(([label, description]) => {
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Function);
        item.detail = "ToSh built-in";
        item.documentation = description;
        return item;
    });
}

function buildSpecialVariableCompletions() {
    return Object.entries(languageData.specialVariables).map(([label, description]) => {
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Variable);
        item.detail = "ToSh special variable";
        item.documentation = description;
        return item;
    });
}

function buildDeclaredSymbolCompletions(document) {
    const completions = [];
    const seen = new Set();
    const patterns = [
        { regex: /^\s*(?:(?:shy|global|export)\s+)?class\s+([A-Za-z_][A-Za-z0-9_-]*)/gm, kind: vscode.CompletionItemKind.Class },
        { regex: /^\s*(?:(?:shy|global|export)\s+)?module\s+([A-Za-z_][A-Za-z0-9_-]*)/gm, kind: vscode.CompletionItemKind.Module },
        { regex: /^\s*(?:(?:shy|global|export)\s+)?enum\s+([A-Za-z_][A-Za-z0-9_-]*)/gm, kind: vscode.CompletionItemKind.Enum },
        { regex: /^\s*(?:(?:shy|global|export)\s+)?record\s+([A-Za-z_][A-Za-z0-9_-]*)/gm, kind: vscode.CompletionItemKind.Struct },
        { regex: /^\s*(?:(?:shy|global|export)\s+)?func\s+([A-Za-z_][A-Za-z0-9_-]*)/gm, kind: vscode.CompletionItemKind.Function },
        { regex: /^\s*(?:(?:shy|global|export)\s+)?var\s+([A-Za-z_][A-Za-z0-9_]*)/gm, kind: vscode.CompletionItemKind.Variable }
    ];

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
    const regex = /^\s*(?:(?:shy|global|export)\s+)?var\s+([A-Za-z_][A-Za-z0-9_]*)/gm;
    let match;

    while ((match = regex.exec(document.getText())) !== null) {
        const label = `$${match[1]}`;
        if (seen.has(label)) {
            continue;
        }

        seen.add(label);
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Variable);
        item.detail = "Variable declared in current document";
        completions.push(item);
    }

    return completions;
}

module.exports = {
    activate,
    deactivate
};
