using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UCanAccess.File;

namespace UCanAccess.AccessCrypto;

internal sealed class AccessCryptoPageCodecFactory : IAccessPageCodecFactory
{
    private readonly string _password;

    public AccessCryptoPageCodecFactory(string password)
    {
        _password = password;
    }

    public IAccessPageCodec Create(AccessPageCodecContext context)
    {
        byte[] root = context.RawRootPage.ToArray();
        if (root.Length < context.Format.PageSize)
        {
            throw new AccessEncryptionException("The Access root page is shorter than the declared page size.");
        }

        bool hasEncryptionInfo = LooksLikeAgileDescriptor(root);
        RootCandidate candidate = FindRootCandidate(root, context.Format);
        if (candidate.EncodingKey == 0)
        {
            if (hasEncryptionInfo)
            {
                throw new AccessEncryptionException(
                    "The Access EncryptionInfo stream has no usable encoding key.");
            }
            // The adapter is also useful in applications which always install
            // an opener.  A plaintext database remains a normal no-op file.
            return new PlainPageCodec();
        }

        if (context.Format.Name is not ("VERSION_14" or "VERSION_16"))
        {
            throw new AccessEncryptionException(
                "Pure .NET Access encryption currently supports encrypted ACCDB formats from Access 2010 and later.");
        }

        EncryptionDescriptor descriptor = ParseDescriptor(root);
        byte[] masterKey = descriptor.UnwrapMasterKey(_password);
        try
        {
            return new AgileAccessPageCodec(
                context.Format.PageSize,
                candidate.EncodingKey,
                descriptor.KeyDataSalt,
                descriptor.KeyDataBlockSize,
                descriptor.KeyDataKeyBits,
                descriptor.KeyDataHash,
                masterKey,
                candidate.RootTransform,
                context.Format.OffsetMaskedHeader,
                context.Format.HeaderMask);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static RootCandidate FindRootCandidate(byte[] raw, JetFormat format)
    {
        if (!LooksLikeAgileDescriptor(raw))
        {
            return new RootCandidate(0, AccessRootTransform.None);
        }

        // Access stores the protected 0x18..0x97 header region through a
        // fixed RC4 stream (0x6b39dac7, little-endian).  PageChannel exposes
        // the ordinary Jet header-mask contract to the rest of the file
        // layer, so the codec compensates for that mask when it is installed.
        // Keep the alternate orderings as diagnostics for older files, but
        // prefer the current Access representation.
        foreach (AccessRootTransform transform in new[]
        {
            AccessRootTransform.Rc4Only,
            AccessRootTransform.Rc4ThenHeaderMask,
            AccessRootTransform.HeaderMaskThenRc4,
            AccessRootTransform.None,
        })
        {
            byte[] candidate = raw.ToArray();
            ApplyRootTransform(candidate, format, transform);
            if (!HasRootPageSignature(candidate, format))
            {
                continue;
            }
            uint encodingKey = BinaryPrimitives.ReadUInt32LittleEndian(candidate.AsSpan(0x3E, 4));
            if (encodingKey != 0)
            {
                return new RootCandidate(encodingKey, transform);
            }
        }

        return new RootCandidate(0, AccessRootTransform.None);
    }

    private static bool HasRootPageSignature(byte[] page, JetFormat format)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x00, 0x01, 0x00, 0x00 };
        return page.AsSpan(format.OffsetMaskedHeader, signature.Length).SequenceEqual(signature);
    }

    private static bool LooksLikeAgileDescriptor(byte[] root)
    {
        const int offset = 0x299;
        if (root.Length < offset + 10) return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(offset, 2));
        if (length <= 8 || offset + 2 + length > root.Length) return false;
        return FindXmlOffset(root.AsSpan(offset + 2, length)) >= 0;
    }

    private static void ApplyRootTransform(byte[] page, JetFormat format,
        AccessRootTransform transform)
    {
        Span<byte> protectedRegion = page.AsSpan(format.OffsetMaskedHeader, format.HeaderMask.Length);
        switch (transform)
        {
            case AccessRootTransform.Rc4ThenHeaderMask:
                Rc4Transform(protectedRegion);
                XorHeaderMask(protectedRegion, format.HeaderMask);
                break;
            case AccessRootTransform.HeaderMaskThenRc4:
                XorHeaderMask(protectedRegion, format.HeaderMask);
                Rc4Transform(protectedRegion);
                break;
            case AccessRootTransform.Rc4Only:
                Rc4Transform(protectedRegion);
                break;
            case AccessRootTransform.None:
                // The ordinary Jet header mask is still present when no
                // additional RC4 transform is used.
                XorHeaderMask(protectedRegion, format.HeaderMask);
                break;
        }
    }

    private static void Rc4Transform(Span<byte> data)
    {
        Span<byte> key = stackalloc byte[] { 0xC7, 0xDA, 0x39, 0x6B };
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < state.Length; i++) state[i] = (byte)i;

        int j = 0;
        for (int i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        int x = 0;
        j = 0;
        for (int i = 0; i < data.Length; i++)
        {
            x = (x + 1) & 0xFF;
            j = (j + state[x]) & 0xFF;
            (state[x], state[j]) = (state[j], state[x]);
            data[i] ^= state[(state[x] + state[j]) & 0xFF];
        }
    }

    private static void XorHeaderMask(Span<byte> region, byte[] mask)
    {
        for (int i = 0; i < mask.Length && i < region.Length; i++)
        {
            region[i] ^= mask[i];
        }
    }

    private static EncryptionDescriptor ParseDescriptor(byte[] root)
    {
        const int offset = 0x299;
        if (root.Length < offset + 2)
        {
            throw new AccessEncryptionException("The encrypted Access header has no EncryptionInfo length.");
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(offset, 2));
        if (length <= 8 || offset + 2 + length > root.Length)
        {
            throw new AccessEncryptionException("The Access EncryptionInfo stream is invalid or truncated.");
        }

        ReadOnlySpan<byte> info = root.AsSpan(offset + 2, length);
        if (info.Length < 8
            || BinaryPrimitives.ReadUInt16LittleEndian(info[..2]) != 4
            || BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(2, 2)) != 4
            || BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(4, 4)) != 0x40)
        {
            throw new AccessEncryptionException(
                "The Access EncryptionInfo stream is not an Agile 4.4 descriptor.");
        }
        int xmlOffset = FindXmlOffset(info);
        if (xmlOffset < 0)
        {
            throw new AccessEncryptionException("The Access EncryptionInfo stream does not contain an Agile descriptor.");
        }

        try
        {
            string xml = Encoding.UTF8.GetString(info[xmlOffset..]).TrimEnd('\0');
            XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XElement keyData = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "keyData")
                ?? throw new AccessEncryptionException("The Agile descriptor has no keyData element.");
            XElement keyEncryptors = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "keyEncryptors")
                ?? throw new AccessEncryptionException("The Agile descriptor has no keyEncryptors element.");
            List<XElement> encryptors = keyEncryptors.Elements()
                .Where(e => e.Name.LocalName == "keyEncryptor")
                .ToList();
            if (encryptors.Count != 1
                || !string.Equals(encryptors[0].Attribute("uri")?.Value,
                    "http://schemas.microsoft.com/office/2006/keyEncryptor/password",
                    StringComparison.Ordinal))
            {
                throw new AccessEncryptionException(
                    "Only a single password key encryptor is supported by the Access Agile codec.");
            }
            XElement encryptedKey = encryptors[0].Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "encryptedKey")
                ?? throw new AccessEncryptionException("The Agile descriptor has no encryptedKey element.");

            return EncryptionDescriptor.FromXml(keyData, encryptedKey);
        }
        catch (AccessEncryptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Xml.XmlException)
        {
            throw new AccessEncryptionException("The Access EncryptionInfo XML is invalid.", ex);
        }
    }

    private static int FindXmlOffset(ReadOnlySpan<byte> info)
    {
        ReadOnlySpan<byte> marker = "<encryption"u8;
        for (int i = 0; i <= info.Length - marker.Length; i++)
        {
            if (info[i..(i + marker.Length)].SequenceEqual(marker)) return i;
        }
        return -1;
    }

    private readonly record struct RootCandidate(uint EncodingKey, AccessRootTransform RootTransform);

    private sealed class PlainPageCodec : IAccessPageCodec
    {
        public void DecodePage(int pageNumber, ReadOnlySpan<byte> encrypted, Span<byte> plaintext)
            => encrypted.CopyTo(plaintext);

        public void EncodePage(int pageNumber, ReadOnlySpan<byte> plaintext, Span<byte> encrypted)
            => plaintext.CopyTo(encrypted);

        public void Dispose()
        {
        }
    }
}
