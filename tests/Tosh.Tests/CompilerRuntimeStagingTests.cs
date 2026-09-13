using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class CompilerRuntimeStagingTests
{
    [Fact]
    public void Ordinary_IL_is_copied_byte_for_byte_and_unchanged_output_is_not_rewritten()
    {
        using var files = new StagingFiles();
        var original = EmitProgram();
        File.WriteAllBytes(files.Source, original);

        ToshPublisher.StageRuntimeDependency(files.Source, files.Destination);
        var timestamp = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(files.Destination, timestamp);
        timestamp = File.GetLastWriteTimeUtc(files.Destination);
        ToshPublisher.StageRuntimeDependency(files.Source, files.Destination);

        Assert.Equal(original, File.ReadAllBytes(files.Source));
        Assert.Equal(original, File.ReadAllBytes(files.Destination));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(files.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composite_component_becomes_neutral_IL_without_changing_metadata_or_method_bodies(bool pe32Plus)
    {
        using var files = new StagingFiles();
        var component = CreateComponent(EmitProgram(), pe32Plus);
        File.WriteAllBytes(files.Source, component);

        ToshPublisher.StageRuntimeDependency(files.Source, files.Destination);

        Assert.Equal(component, File.ReadAllBytes(files.Source));
        using var before = new PEReader(new MemoryStream(component));
        using var after = new PEReader(File.OpenRead(files.Destination));
        Assert.Equal(PEMagic.PE32, after.PEHeaders.PEHeader!.Magic);
        Assert.Equal(Machine.I386, after.PEHeaders.CoffHeader.Machine);
        Assert.True(after.PEHeaders.CorHeader!.Flags.HasFlag(CorFlags.ILOnly));
        Assert.False(after.PEHeaders.CorHeader.Flags.HasFlag(CorFlags.ILLibrary));
        Assert.Equal(0, after.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size);
        Assert.Equal(0, after.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.RelativeVirtualAddress);
        Assert.Equal(before.GetMetadata().GetContent().ToArray(), after.GetMetadata().GetContent().ToArray());
        Assert.Equal(before.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress,
            after.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress);
        Assert.Equal(before.PEHeaders.CorHeader.ResourcesDirectory, after.PEHeaders.CorHeader.ResourcesDirectory);

        foreach (var handle in before.GetMetadataReader().MethodDefinitions)
        {
            var method = before.GetMetadataReader().GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var oldBody = before.GetMethodBody(method.RelativeVirtualAddress);
            var newBody = after.GetMethodBody(method.RelativeVirtualAddress);
            Assert.Equal(oldBody.GetILBytes(), newBody.GetILBytes());
            Assert.Equal(oldBody.LocalSignature, newBody.LocalSignature);
            Assert.Equal(oldBody.ExceptionRegions.ToArray(), newBody.ExceptionRegions.ToArray());
        }
    }

    [Theory]
    [InlineData("platform-specific")]
    [InlineData("strong-name")]
    [InlineData("strong-name-directory")]
    [InlineData("authenticode")]
    [InlineData("native-entrypoint")]
    [InlineData("requires32bit")]
    public void Unsafe_component_is_rejected_before_an_existing_output_is_changed(string reason)
    {
        using var files = new StagingFiles();
        var component = CreateComponent(EmitProgram(), pe32Plus: false);
        using (var pe = new PEReader(new MemoryStream(component)))
        {
            var corOffset = pe.PEHeaders.CorHeaderStartOffset;
            var optionalOffset = pe.PEHeaders.CoffHeaderStartOffset + 20;
            var flags = pe.PEHeaders.CorHeader!.Flags;
            switch (reason)
            {
                case "platform-specific": Write32(component, component.Length - 512 + 8, 0x20); break;
                case "strong-name": Write32(component, corOffset + 16, (uint)(flags | CorFlags.StrongNameSigned)); break;
                case "strong-name-directory": Write32(component, corOffset + 36, 16); break;
                case "authenticode": Write32(component, optionalOffset + 96 + 4 * 8 + 4, 16); break;
                case "native-entrypoint": Write32(component, corOffset + 16, (uint)(flags | CorFlags.NativeEntryPoint)); break;
                case "requires32bit": Write32(component, corOffset + 16, (uint)(flags | CorFlags.Requires32Bit)); break;
            }
        }
        File.WriteAllBytes(files.Source, component);
        var existing = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(files.Destination, existing);

        Assert.Throws<NotSupportedException>(() => ToshPublisher.StageRuntimeDependency(files.Source, files.Destination));

        Assert.Equal(component, File.ReadAllBytes(files.Source));
        Assert.Equal(existing, File.ReadAllBytes(files.Destination));
        Assert.Empty(Directory.GetFiles(files.Directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Non_component_native_headers_are_not_rewritten(bool readyToRun)
    {
        using var files = new StagingFiles();
        var original = CreateComponent(EmitProgram(), pe32Plus: false);
        if (readyToRun) Write32(original, original.Length - 512 + 8, 1); // Not a component.
        else Write32(original, original.Length - 512, 0x12345678); // Not ReadyToRun.
        File.WriteAllBytes(files.Source, original);

        ToshPublisher.StageRuntimeDependency(files.Source, files.Destination);

        Assert.Equal(original, File.ReadAllBytes(files.Destination));
    }

    [Fact]
    public void Same_path_does_not_rewrite_the_compilers_own_component()
    {
        using var files = new StagingFiles();
        var original = CreateComponent(EmitProgram(), pe32Plus: true);
        File.WriteAllBytes(files.Source, original);

        ToshPublisher.StageRuntimeDependency(files.Source, files.Source);

        Assert.Equal(original, File.ReadAllBytes(files.Source));
    }

    [Fact]
    public void Legacy_component_output_is_repaired_even_when_it_has_a_newer_timestamp()
    {
        using var files = new StagingFiles();
        var component = CreateComponent(EmitProgram(), pe32Plus: true);
        File.WriteAllBytes(files.Source, component);
        File.WriteAllBytes(files.Destination, component);
        File.SetLastWriteTimeUtc(files.Destination, DateTime.UtcNow.AddDays(1));

        ToshPublisher.StageRuntimeDependency(files.Source, files.Destination);

        using var pe = new PEReader(File.OpenRead(files.Destination));
        Assert.Equal(0, pe.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size);
    }

    [Fact]
    public async Task Detached_component_executes_without_its_native_owner_or_environment_overrides()
    {
        using var files = new StagingFiles();
        File.WriteAllBytes(files.Source, CreateComponent(EmitProgram(), pe32Plus: true));
        ToshPublisher.StageRuntimeDependency(files.Source, files.Destination);
        foreach (var dependency in ToshPublisher.GetRuntimeDependencyFileNames())
        {
            ToshPublisher.StageRuntimeDependency(Path.Combine(AppContext.BaseDirectory, dependency),
                Path.Combine(files.Directory, dependency));
        }
        File.WriteAllText(Path.ChangeExtension(files.Destination, ".runtimeconfig.json"), $$"""
            { "runtimeOptions": { "tfm": "net{{Environment.Version.Major}}.0",
              "framework": { "name": "Microsoft.NETCore.App", "version": "{{Environment.Version}}" } } }
            """);
        ToshPublisher.WriteDepsJson(files.Destination);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = files.Directory,
            },
        };
        process.StartInfo.ArgumentList.Add(files.Destination);
        // The assertion must exercise normal native-image probing even if the test runner
        // was started under a diagnostic override. The child alone gets a clean setting.
        process.StartInfo.Environment.Remove("DOTNET_ReadyToRun");
        process.StartInfo.Environment.Remove("COMPlus_ReadyToRun");
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The detached ReadyToRun component did not exit.");
        }

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("staged component ready", (await stdout).Trim());
        Assert.Empty(await stderr);
        Assert.False(File.Exists(Path.Combine(files.Directory, "missing-owner.r2r.dll")));
    }

    private static byte[] EmitProgram()
    {
        var runtime = ToshRuntime.CreateDefault();
        var parse = new ToshEngine(runtime.Language).Parse("echo \"staged component ready\"", "staging.tosh");
        Assert.Empty(parse.Diagnostics);
        var unit = Lowerer.Lower(parse, runtime.Commands);
        using var output = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, "StagedComponent", output);
        Assert.True(result.IsClean, string.Join("; ", result.UnsupportedShapes));
        return output.ToArray();
    }

    // Construct the documented standalone component envelope around real emitted IL.
    // Only the fixture's PE headers and one appended section tail differ; metadata/IL
    // remain real. This does not require a published CLI or Crossgen2 in unit-test runs.
    private static byte[] CreateComponent(byte[] original, bool pe32Plus)
    {
        using var pe = new PEReader(new MemoryStream(original));
        var headers = pe.PEHeaders;
        var image = new byte[original.Length + 512];
        original.CopyTo(image, 0);
        // PersistedAssemblyBuilder reserves a blank strong-name slot even for an
        // unsigned assembly. Actual unsigned published compiler components have no
        // such directory. Remove only the fixture's verified-empty reservation;
        // production staging deliberately rejects any strong-name directory.
        Assert.False(headers.CorHeader!.Flags.HasFlag(CorFlags.StrongNameSigned));
        Assert.True(pe.GetMetadataReader().GetAssemblyDefinition().PublicKey.IsNil);
        var signature = headers.CorHeader.StrongNameSignatureDirectory;
        if (signature.Size != 0)
        {
            Assert.All(pe.GetSectionData(signature.RelativeVirtualAddress).GetContent(0, signature.Size),
                value => Assert.Equal(0, value));
            image.AsSpan(headers.CorHeaderStartOffset + 32, 8).Clear();
        }
        var last = headers.SectionHeaders[^1];
        var dataRva = last.VirtualAddress + original.Length - last.PointerToRawData;
        var sectionTable = headers.CoffHeaderStartOffset + 20 + headers.CoffHeader.SizeOfOptionalHeader;
        var lastSection = sectionTable + (headers.SectionHeaders.Length - 1) * 40;
        Write32(image, lastSection + 8, (uint)(dataRva + 512 - last.VirtualAddress));
        Write32(image, lastSection + 16, (uint)(image.Length - last.PointerToRawData));
        var optionalOffset = headers.CoffHeaderStartOffset + 20;
        var alignment = headers.PEHeader!.SectionAlignment;
        Write32(image, optionalOffset + 56, (uint)((dataRva + 512 + alignment - 1) / alignment * alignment));
        var native = original.Length;
        Write32(image, native, 0x00525452);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(native + 4), 16);
        Write32(image, native + 8, 0x21); // PLATFORM_NEUTRAL_SOURCE | COMPONENT.
        Write32(image, native + 12, 1);
        Write32(image, native + 16, 116); // OwnerCompositeExecutable.
        Write32(image, native + 20, (uint)(dataRva + 28));
        var owner = Encoding.UTF8.GetBytes("missing-owner.r2r.dll\0");
        Write32(image, native + 24, (uint)owner.Length);
        owner.CopyTo(image, native + 28);
        Write32(image, headers.CorHeaderStartOffset + 64, (uint)dataRva);
        Write32(image, headers.CorHeaderStartOffset + 68, 28);
        Write32(image, headers.CorHeaderStartOffset + 16,
            (uint)((headers.CorHeader!.Flags & ~CorFlags.ILOnly) | CorFlags.ILLibrary));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headers.CoffHeaderStartOffset), 0xFD1D); // Linux AMD64 R2R.

        if (pe32Plus)
        {
            Assert.Equal(PEMagic.PE32, headers.PEHeader.Magic);
            var optionalSize = headers.CoffHeader.SizeOfOptionalHeader;
            var sectionBytes = headers.SectionHeaders.Length * 40;
            Assert.True(sectionTable + sectionBytes + 16 <= headers.PEHeader.SizeOfHeaders);
            image.AsSpan(sectionTable, sectionBytes).CopyTo(image.AsSpan(sectionTable + 16));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headers.CoffHeaderStartOffset + 16), (ushort)(optionalSize + 16));
            var optional = image.AsSpan(optionalOffset, optionalSize + 16);
            var old = optional.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(optional, (ushort)PEMagic.PE32Plus);
            BinaryPrimitives.WriteUInt64LittleEndian(optional[24..], 0x180000000);
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(optional[(72 + i * 8)..],
                    BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(72 + i * 4)));
            old.AsSpan(88, 8).CopyTo(optional[104..]);
            old.AsSpan(96, optionalSize - 96).CopyTo(optional[112..]);
        }
        return image;
    }

    private static void Write32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed class StagingFiles : IDisposable
    {
        private readonly DirectoryInfo root = System.IO.Directory.CreateTempSubdirectory("tosh-runtime-staging-");
        public string Directory => root.FullName;
        public string Source => Path.Combine(root.FullName, "original.dll");
        public string Destination => Path.Combine(root.FullName, "StagedComponent.dll");
        public void Dispose() => root.Delete(recursive: true);
    }
}
