using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UCanAccess.AccessCrypto;
using UCanAccess.File;
using Xunit;

namespace UCanAccess.AccessCrypto.Tests;

public sealed class CodecTests
{
    [Fact]
    public void Optional_opener_keeps_plaintext_accdb_compatible()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-plain-{Guid.NewGuid():N}.accdb");
        try
        {
            using (Database.Create(path, version: "2010"))
            {
            }

            using Database database = new AccessCryptoOpener().Open(new UCanAccess.AccessDatabaseOpenRequest(
                path, ReadOnly: true, Encoding: null, AllowExternalLinks: false, Password: null));
            Assert.Equal("VERSION_14", database.Format.Name);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            string lockPath = Path.ChangeExtension(path, ".laccdb");
            if (System.IO.File.Exists(lockPath)) System.IO.File.Delete(lockPath);
        }
    }

    [Fact]
    public void Agile_page_codec_round_trips_each_page_number()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] salt = Enumerable.Range(32, 16).Select(i => (byte)i).ToArray();
        using var codec = new AgileAccessPageCodec(
            4096, 0x78563412, salt, 16, 256, HashAlgorithmName.SHA512,
            key, AccessRootTransform.None);

        byte[] plaintext = new byte[4096];
        RandomNumberGenerator.Fill(plaintext);
        byte[] encrypted = new byte[4096];
        byte[] decoded = new byte[4096];

        codec.EncodePage(1, plaintext, encrypted);
        Assert.NotEqual(plaintext, encrypted);
        codec.DecodePage(1, encrypted, decoded);
        Assert.Equal(plaintext, decoded);

        codec.EncodePage(37, plaintext, encrypted);
        codec.DecodePage(37, encrypted, decoded);
        Assert.Equal(plaintext, decoded);

        // PageChannel applies the Jet mask around root-page codec calls.  The
        // RC4 and mask operations must therefore be inverse in opposite
        // orders when a root page is written and read.
        using var rootCodec = new AgileAccessPageCodec(
            4096, 0x78563412, salt, 16, 256, HashAlgorithmName.SHA512,
            key, AccessRootTransform.Rc4Only, JetFormat.Version14.OffsetMaskedHeader,
            JetFormat.Version14.HeaderMask);
        byte[] logicalRoot = new byte[4096];
        RandomNumberGenerator.Fill(logicalRoot);
        byte[] maskedRoot = logicalRoot.ToArray();
        Xor(maskedRoot.AsSpan(JetFormat.Version14.OffsetMaskedHeader,
            JetFormat.Version14.HeaderMask.Length), JetFormat.Version14.HeaderMask);
        byte[] encodedRoot = new byte[4096];
        rootCodec.EncodePage(0, maskedRoot, encodedRoot);
        byte[] decodedRoot = new byte[4096];
        rootCodec.DecodePage(0, encodedRoot, decodedRoot);
        Xor(decodedRoot.AsSpan(JetFormat.Version14.OffsetMaskedHeader,
            JetFormat.Version14.HeaderMask.Length), JetFormat.Version14.HeaderMask);
        Assert.Equal(logicalRoot, decodedRoot);
    }

    [Fact]
    public void Synthetic_agile_database_is_readable_and_writable_through_database_api()
    {
        const string password = "Synthetic!2026";
        string path = Path.Combine(Path.GetTempPath(), $"uca-synthetic-{Guid.NewGuid():N}.accdb");
        try
        {
            using (Database database = Database.Create(path, version: "2010"))
            {
                Table table = database.CreateTable("Crypto", new[]
                {
                    new ColumnBuilder("Code", DataType.Text).WithLength(80),
                });
                table.AddRow(new object?[] { "before-encryption" });
            }

            CreateSyntheticEncryptedImage(path, password);
            var request = new UCanAccess.AccessDatabaseOpenRequest(
                path, ReadOnly: false, Encoding: null, AllowExternalLinks: false, Password: password);
            using (Database encrypted = new AccessCryptoOpener().Open(request))
            {
                Assert.Contains("Crypto", encrypted.GetTableNames());
                encrypted.GetTable("Crypto")!.AddRow(new object?[] { "after-encryption" });
            }

            using Database reopened = new AccessCryptoOpener().Open(request with { ReadOnly = true });
            Assert.Equal(2, reopened.GetTable("Crypto")!.RowCount);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            string lockPath = Path.ChangeExtension(path, ".laccdb");
            if (System.IO.File.Exists(lockPath)) System.IO.File.Delete(lockPath);
        }
    }

    [Fact]
    public void Agile_password_descriptor_unwraps_master_key()
    {
        const string password = "Pąssw0rd!";
        byte[] passwordSalt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] pageSalt = Enumerable.Range(17, 16).Select(i => (byte)i).ToArray();
        byte[] verifierInput = Encoding.UTF8.GetBytes("access-verifier").Concat(new byte[1]).ToArray();
        byte[] masterKey = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray();
        byte[] verifierHash = SHA512.HashData(verifierInput);

        byte[] Encrypt(byte[] value, byte[] blockKey)
        {
            byte[] key = Derive(password, passwordSalt, 1, blockKey);
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.IV = passwordSalt;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            return aes.CreateEncryptor().TransformFinalBlock(value, 0, value.Length);
        }

        byte[] inputBlock = Encrypt(verifierInput,
            new byte[] { 0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79 });
        byte[] hashBlock = Encrypt(verifierHash,
            new byte[] { 0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E });
        byte[] keyBlock = Encrypt(masterKey,
            new byte[] { 0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6 });

        var keyData = new XElement("keyData",
            new XAttribute("saltValue", Convert.ToBase64String(pageSalt)),
            new XAttribute("blockSize", "16"),
            new XAttribute("keyBits", "256"),
            new XAttribute("cipherAlgorithm", "AES"),
            new XAttribute("cipherChaining", "ChainingModeCBC"),
            new XAttribute("hashAlgorithm", "SHA512"));
        var encryptedKey = new XElement("encryptedKey",
            new XAttribute("saltValue", Convert.ToBase64String(passwordSalt)),
            new XAttribute("blockSize", "16"),
            new XAttribute("keyBits", "256"),
            new XAttribute("spinCount", "1"),
            new XAttribute("hashAlgorithm", "SHA512"),
            new XAttribute("cipherAlgorithm", "AES"),
            new XAttribute("cipherChaining", "ChainingModeCBC"),
            new XAttribute("encryptedVerifierHashInput", Convert.ToBase64String(inputBlock)),
            new XAttribute("encryptedVerifierHashValue", Convert.ToBase64String(hashBlock)),
            new XAttribute("encryptedKeyValue", Convert.ToBase64String(keyBlock)));

        EncryptionDescriptor descriptor = EncryptionDescriptor.FromXml(keyData, encryptedKey);
        Assert.Equal(masterKey, descriptor.UnwrapMasterKey(password));
        Assert.Throws<AccessEncryptionException>(() => descriptor.UnwrapMasterKey("wrong"));
    }

    private static byte[] Derive(string password, byte[] salt, int spinCount, byte[] blockKey)
    {
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] state = SHA512.HashData(salt.Concat(passwordBytes).ToArray());
        for (int i = 0; i < spinCount; i++)
        {
            byte[] counter = BitConverter.GetBytes(i);
            state = SHA512.HashData(counter.Concat(state).ToArray());
        }
        return SHA512.HashData(state.Concat(blockKey).ToArray()).Take(32).ToArray();
    }

    private static void CreateSyntheticEncryptedImage(string path, string password)
    {
        const uint encodingKey = 0x78563412;
        byte[] passwordSalt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] pageSalt = Enumerable.Range(17, 16).Select(i => (byte)i).ToArray();
        byte[] masterKey = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray();
        byte[] verifierInput = Encoding.UTF8.GetBytes("synthetic-verifier").Concat(new byte[14]).ToArray();
        byte[] verifierHash = SHA512.HashData(verifierInput);

        byte[] EncryptValue(byte[] value, byte[] blockKey)
        {
            byte[] key = Derive(password, passwordSalt, 1, blockKey);
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.IV = passwordSalt;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            return aes.CreateEncryptor().TransformFinalBlock(value, 0, value.Length);
        }

        string B64(byte[] value) => Convert.ToBase64String(value);
        string xml = $"<encryption xmlns='http://schemas.microsoft.com/office/2006/encryption' "
            + $"xmlns:p='http://schemas.microsoft.com/office/2006/keyEncryptor/password'>"
            + $"<keyData saltSize='16' blockSize='16' keyBits='256' hashSize='64' cipherAlgorithm='AES' cipherChaining='ChainingModeCBC' hashAlgorithm='SHA512' saltValue='{B64(pageSalt)}'/>"
            + $"<keyEncryptors><keyEncryptor uri='http://schemas.microsoft.com/office/2006/keyEncryptor/password'><p:encryptedKey spinCount='1' saltSize='16' blockSize='16' keyBits='256' hashSize='64' cipherAlgorithm='AES' cipherChaining='ChainingModeCBC' hashAlgorithm='SHA512' saltValue='{B64(passwordSalt)}' encryptedVerifierHashInput='{B64(EncryptValue(verifierInput, new byte[] { 0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79 }))}' encryptedVerifierHashValue='{B64(EncryptValue(verifierHash, new byte[] { 0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E }))}' encryptedKeyValue='{B64(EncryptValue(masterKey, new byte[] { 0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6 }))}'/></keyEncryptor></keyEncryptors></encryption>";
        byte[] info = new byte[8 + Encoding.UTF8.GetByteCount(xml)];
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4, 4), 0x40);
        Encoding.UTF8.GetBytes(xml).CopyTo(info, 8);

        byte[] image = System.IO.File.ReadAllBytes(path);
        JetFormat format = JetFormat.Version14;
        byte[] root = image.AsSpan(0, format.PageSize).ToArray();
        Xor(root.AsSpan(format.OffsetMaskedHeader, format.HeaderMask.Length), format.HeaderMask);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(0x3E, 4), encodingKey);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(0x299, 2), checked((ushort)info.Length));
        info.CopyTo(root, 0x29B);
        Rc4(root.AsSpan(format.OffsetMaskedHeader, format.HeaderMask.Length));
        root.CopyTo(image, 0);

        using var codec = new AgileAccessPageCodec(format.PageSize, encodingKey,
            pageSalt, 16, 256, HashAlgorithmName.SHA512,
            masterKey, AccessRootTransform.None);
        int pageCount = image.Length / format.PageSize;
        for (int pageNumber = 1; pageNumber < pageCount; pageNumber++)
        {
            byte[] plaintext = image.AsSpan(pageNumber * format.PageSize, format.PageSize).ToArray();
            byte[] encrypted = new byte[format.PageSize];
            codec.EncodePage(pageNumber, plaintext, encrypted);
            encrypted.CopyTo(image, pageNumber * format.PageSize);
        }
        System.IO.File.WriteAllBytes(path, image);
    }

    private static void Xor(Span<byte> region, byte[] mask)
    {
        for (int i = 0; i < mask.Length; i++) region[i] ^= mask[i];
    }

    private static void Rc4(Span<byte> data)
    {
        Span<byte> key = stackalloc byte[] { 0xC7, 0xDA, 0x39, 0x6B };
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < state.Length; i++) state[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % 4]) & 0xFF;
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
}
