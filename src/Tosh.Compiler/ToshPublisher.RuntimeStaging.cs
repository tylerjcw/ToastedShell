using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Tosh.Compiler;

public static partial class ToshPublisher
{
    /// <summary>
    /// Copies one compiler runtime dependency into an application output directory.
    /// Composite ReadyToRun components from a published compiler are restored to their
    /// retained IL representation: their native owner belongs to the compiler's exact
    /// framework version bubble, not to the application being built.
    /// </summary>
    public static void StageRuntimeDependency(string sourcePath, string destinationPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourcePath, destinationPath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            // Never rewrite the compiler's own dependency in place.
            return;
        }

        var image = File.ReadAllBytes(sourcePath);
        NormalizeCompositeRuntimeComponent(image, sourcePath);
        if (File.Exists(destinationPath) && File.ReadAllBytes(destinationPath).AsSpan().SequenceEqual(image))
        {
            return;
        }

        // Validate before replacing anything, and do not leave a truncated old dependency
        // if writing fails. Content comparison above also repairs legacy staged components
        // whose timestamp is newer than the source; timestamps alone cannot identify them.
        var temporaryPath = Path.Combine(Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, image);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void NormalizeCompositeRuntimeComponent(byte[] image, string sourcePath)
    {
        using var reader = new PEReader(new MemoryStream(image, writable: false));
        var headers = reader.PEHeaders;
        var corHeader = headers.CorHeader;
        if (corHeader is null || corHeader.ManagedNativeHeaderDirectory.Size == 0) return;

        var nativeHeader = corHeader.ManagedNativeHeaderDirectory;
        var data = reader.GetSectionData(nativeHeader.RelativeVirtualAddress).GetReader();
        // These are the documented READYTORUN_HEADER/CoreHeader fields. Do not treat an
        // arbitrary managed-native header (e.g. NGen) as a ReadyToRun image.
        // https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-format.md
        const uint readyToRunSignature = 0x00525452;
        const uint platformNeutralSource = 0x00000001;
        const uint component = 0x00000020;
        if (nativeHeader.Size < 16 || data.RemainingBytes < 16 || data.ReadUInt32() != readyToRunSignature) return;
        _ = data.ReadUInt16(); // Major/minor do not change the fixed header prefix.
        _ = data.ReadUInt16();
        var readyToRunFlags = data.ReadUInt32();
        if ((readyToRunFlags & component) == 0) return;

        var metadata = reader.GetMetadataReader();
        var peHeader = headers.PEHeader!;
        if ((readyToRunFlags & platformNeutralSource) == 0
            || (corHeader.Flags & (CorFlags.StrongNameSigned | CorFlags.NativeEntryPoint | CorFlags.Requires32Bit)) != 0
            || corHeader.StrongNameSignatureDirectory.Size != 0
            || peHeader.CertificateTableDirectory.Size != 0
            || (metadata.IsAssembly && !metadata.GetAssemblyDefinition().PublicKey.IsNil))
        {
            throw new NotSupportedException(
                $"Cannot stage composite ReadyToRun dependency '{sourcePath}': only unsigned, " +
                "platform-neutral IL components can be detached from their native owner. " +
                "Use ordinary IL compiler runtime dependencies for this build.");
        }

        // Composite component images retain the original IL, metadata, and resources.
        // Change only PE/CLI headers: no metadata tokens, MVIDs, or method bodies change.
        // Restore a standard neutral PE32 envelope rather than leaving the OS-encoded
        // ReadyToRun machine value or an inconsistent I386/PE32+ header combination.
        RestoreNeutralPeHeader(image, headers);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headers.CoffHeaderStartOffset), (ushort)Machine.I386);
        var flags = (corHeader.Flags | CorFlags.ILOnly) & ~CorFlags.ILLibrary;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headers.CorHeaderStartOffset + 16), (uint)flags);
        image.AsSpan(headers.CorHeaderStartOffset + 64, 8).Clear(); // ManagedNativeHeader RVA/size.
    }

    private static void RestoreNeutralPeHeader(byte[] image, PEHeaders headers)
    {
        const int coffHeaderSize = 20;
        var offset = headers.CoffHeaderStartOffset + coffHeaderSize;
        var optional = image.AsSpan(offset, headers.CoffHeader.SizeOfOptionalHeader);
        if (headers.PEHeader!.Magic == PEMagic.PE32Plus)
        {
            // PE32+ widens ImageBase and stack/heap fields. The data-directory table moves
            // back 16 bytes in PE32. Move the section table with it: managed PE readers
            // expect it immediately after the directories, without optional-header padding.
            // This only moves header bytes; section data, SizeOfHeaders, and RVAs stay put.
            var original = optional.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(optional, (ushort)PEMagic.PE32);
            BinaryPrimitives.WriteUInt32LittleEndian(optional[24..], 0); // BaseOfData (unused by CLR).
            BinaryPrimitives.WriteUInt32LittleEndian(optional[28..], 0x10000000); // Neutral DLL image base.
            for (var i = 0; i < 4; i++)
            {
                var size = BinaryPrimitives.ReadUInt64LittleEndian(original.AsSpan(72 + i * 8));
                if (size > uint.MaxValue)
                    throw new NotSupportedException("Cannot stage an IL component with a non-neutral stack/heap header.");
                BinaryPrimitives.WriteUInt32LittleEndian(optional[(72 + i * 4)..], (uint)size);
            }
            original.AsSpan(104, 8).CopyTo(optional[88..]); // LoaderFlags, NumberOfRvaAndSizes.
            original.AsSpan(112).CopyTo(optional[96..]);
            optional[^16..].Clear();
            var sectionTable = offset + headers.CoffHeader.SizeOfOptionalHeader;
            var sectionBytes = headers.SectionHeaders.Length * 40;
            image.AsSpan(sectionTable, sectionBytes).CopyTo(image.AsSpan(sectionTable - 16));
            image.AsSpan(sectionTable + sectionBytes - 16, 16).Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headers.CoffHeaderStartOffset + 16),
                (ushort)(headers.CoffHeader.SizeOfOptionalHeader - 16));
        }
        else if (headers.PEHeader.Magic != PEMagic.PE32)
        {
            throw new NotSupportedException("Cannot stage an IL component with an unknown PE optional header.");
        }
        optional.Slice(64, 4).Clear(); // Discard the old native image checksum.
    }
}
