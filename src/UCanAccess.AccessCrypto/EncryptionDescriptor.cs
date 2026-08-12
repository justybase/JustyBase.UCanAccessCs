using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace UCanAccess.AccessCrypto;

internal sealed class EncryptionDescriptor
{
    private const int MaxPasswordSpinCount = 10_000_000;
    private static readonly byte[] BlockKeyHashInput =
        { 0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79 };
    private static readonly byte[] BlockKeyHashValue =
        { 0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E };
    private static readonly byte[] BlockKeyMasterKey =
        { 0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6 };

    private readonly int _passwordKeyBits;
    private readonly int _passwordBlockSize;
    private readonly int _passwordSpinCount;
    private readonly HashAlgorithmName _passwordHash;
    private readonly byte[] _passwordSalt;
    private readonly byte[] _encryptedVerifierHashInput;
    private readonly byte[] _encryptedVerifierHashValue;
    private readonly byte[] _encryptedKeyValue;

    private EncryptionDescriptor(
        int passwordKeyBits,
        int passwordBlockSize,
        int passwordSpinCount,
        HashAlgorithmName passwordHash,
        byte[] passwordSalt,
        byte[] encryptedVerifierHashInput,
        byte[] encryptedVerifierHashValue,
        byte[] encryptedKeyValue,
        byte[] keyDataSalt,
        int keyDataBlockSize,
        int keyDataKeyBits,
        HashAlgorithmName keyDataHash)
    {
        _passwordKeyBits = passwordKeyBits;
        _passwordBlockSize = passwordBlockSize;
        _passwordSpinCount = passwordSpinCount;
        _passwordHash = passwordHash;
        _passwordSalt = passwordSalt;
        _encryptedVerifierHashInput = encryptedVerifierHashInput;
        _encryptedVerifierHashValue = encryptedVerifierHashValue;
        _encryptedKeyValue = encryptedKeyValue;
        KeyDataSalt = keyDataSalt;
        KeyDataBlockSize = keyDataBlockSize;
        KeyDataKeyBits = keyDataKeyBits;
        KeyDataHash = keyDataHash;
    }

    public byte[] KeyDataSalt { get; }

    public int KeyDataBlockSize { get; }

    public int KeyDataKeyBits { get; }

    public HashAlgorithmName KeyDataHash { get; }

    public static EncryptionDescriptor FromXml(XElement keyData, XElement encryptedKey)
    {
        byte[] keyDataSalt = Base64(keyData, "saltValue");
        int keyDataBlockSize = Integer(keyData, "blockSize");
        int keyDataKeyBits = Integer(keyData, "keyBits");
        HashAlgorithmName keyDataHash = ParseHash(keyData.Attribute("hashAlgorithm")?.Value);
        if (keyDataSalt.Length == 0 || keyDataBlockSize != 16 || keyDataKeyBits is not (128 or 192 or 256)
            || !IsAesCbc(keyData))
        {
            throw new AccessEncryptionException("The Access keyData cipher parameters are unsupported.");
        }

        int passwordKeyBits = Integer(encryptedKey, "keyBits");
        int passwordBlockSize = Integer(encryptedKey, "blockSize");
        int spinCount = Integer(encryptedKey, "spinCount");
        HashAlgorithmName hash = ParseHash(encryptedKey.Attribute("hashAlgorithm")?.Value);
        byte[] passwordSalt = Base64(encryptedKey, "saltValue");
        if (passwordKeyBits is not (128 or 192 or 256) || passwordBlockSize != 16
            || spinCount <= 0 || spinCount > MaxPasswordSpinCount
            || passwordSalt.Length == 0 || !IsAesCbc(encryptedKey))
        {
            throw new AccessEncryptionException("The Access password cipher parameters are unsupported.");
        }

        // The page key length is recorded independently of the password key
        // length.  The current Access Agile profile normally uses 256 bits;
        // retaining the parsed value makes 128-bit fixtures interoperable too.
        return new EncryptionDescriptor(
            passwordKeyBits,
            passwordBlockSize,
            spinCount,
            hash,
            passwordSalt,
            Base64(encryptedKey, "encryptedVerifierHashInput"),
            Base64(encryptedKey, "encryptedVerifierHashValue"),
            Base64(encryptedKey, "encryptedKeyValue"),
            keyDataSalt,
            keyDataBlockSize,
            keyDataKeyBits,
            keyDataHash);
    }

    public byte[] UnwrapMasterKey(string password)
    {
        byte[]? verifierInput = null;
        byte[]? verifierHash = null;
        byte[]? masterKey = null;
        try
        {
            verifierInput = DecryptPasswordValue(password, BlockKeyHashInput, _encryptedVerifierHashInput);
            verifierHash = DecryptPasswordValue(password, BlockKeyHashValue, _encryptedVerifierHashValue);
            byte[] computedHash = Hash(verifierInput);
            int verifierLength = checked((computedHash.Length + _passwordBlockSize - 1)
                / _passwordBlockSize * _passwordBlockSize);
            // Jackcess' Agile verifier comparison uses the default
            // zero-filled fixToLength operation (the 0x36 pad is reserved for
            // key/IV derivation).
            byte[] paddedHash = FixToLength(computedHash, verifierLength, 0x00);
            try
            {
                if (verifierHash.Length != paddedHash.Length
                    || !CryptographicOperations.FixedTimeEquals(paddedHash, verifierHash))
                {
                    throw new AccessEncryptionException("The Access database password is incorrect.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(computedHash);
                CryptographicOperations.ZeroMemory(paddedHash);
            }

            masterKey = DecryptPasswordValue(password, BlockKeyMasterKey, _encryptedKeyValue);
            return masterKey.ToArray();
        }
        catch (CryptographicException ex)
        {
            throw new AccessEncryptionException("The Access database password is incorrect.", ex);
        }
        finally
        {
            if (verifierInput != null) CryptographicOperations.ZeroMemory(verifierInput);
            if (verifierHash != null) CryptographicOperations.ZeroMemory(verifierHash);
            if (masterKey != null) CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private byte[] DecryptPasswordValue(string password, byte[] blockKey, byte[] value)
    {
        byte[] key = DerivePasswordKey(password, blockKey);
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.IV = FixIv(_passwordSalt, _passwordBlockSize);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(value, 0, value.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] DerivePasswordKey(string password, byte[] blockKey)
    {
        using HashAlgorithm hash = CreateHash(_passwordHash);
        if (password.Length > 255)
        {
            // Access/Jackcess cap the password material at 255 UTF-16 code
            // units before applying the Agile KDF.
            password = password[..255];
        }
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] initial = new byte[_passwordSalt.Length + passwordBytes.Length];
        Buffer.BlockCopy(_passwordSalt, 0, initial, 0, _passwordSalt.Length);
        Buffer.BlockCopy(passwordBytes, 0, initial, _passwordSalt.Length, passwordBytes.Length);
        CryptographicOperations.ZeroMemory(passwordBytes);
        byte[] state = hash.ComputeHash(initial);
        CryptographicOperations.ZeroMemory(initial);

        Span<byte> counter = stackalloc byte[4];
        byte[] iterationInput = new byte[4 + state.Length];
        try
        {
            for (int i = 0; i < _passwordSpinCount; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(counter, (uint)i);
                counter.CopyTo(iterationInput);
                state.CopyTo(iterationInput, 4);
                byte[] next = hash.ComputeHash(iterationInput);
                CryptographicOperations.ZeroMemory(state);
                state = next;
            }

            byte[] finalInput = new byte[state.Length + blockKey.Length];
            state.CopyTo(finalInput, 0);
            blockKey.CopyTo(finalInput, state.Length);
            byte[] finalHash = hash.ComputeHash(finalInput);
            CryptographicOperations.ZeroMemory(finalInput);
            byte[] key = new byte[_passwordKeyBits / 8];
            if (key.Length <= finalHash.Length)
            {
                finalHash.AsSpan(0, key.Length).CopyTo(key);
            }
            else
            {
                finalHash.CopyTo(key, 0);
                key.AsSpan(finalHash.Length).Fill(0x36);
            }
            CryptographicOperations.ZeroMemory(finalHash);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iterationInput);
            CryptographicOperations.ZeroMemory(state);
        }
    }

    private byte[] Hash(byte[] input)
    {
        using HashAlgorithm hash = CreateHash(_passwordHash);
        return hash.ComputeHash(input);
    }

    private static HashAlgorithm CreateHash(HashAlgorithmName name)
        => name == HashAlgorithmName.SHA512 ? SHA512.Create()
            : name == HashAlgorithmName.SHA256 ? SHA256.Create()
            : name == HashAlgorithmName.SHA1 ? SHA1.Create()
            : throw new AccessEncryptionException($"Unsupported Access hash algorithm '{name.Name}'.");

    private static HashAlgorithmName ParseHash(string? name)
        => name?.ToUpperInvariant() switch
        {
            "SHA512" => HashAlgorithmName.SHA512,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA1" => HashAlgorithmName.SHA1,
            _ => throw new AccessEncryptionException($"Unsupported Access hash algorithm '{name}'."),
        };

    private static byte[] FixIv(byte[] salt, int length)
        => FixToLength(salt, length, 0x36);

    private static byte[] FixToLength(byte[] input, int length, byte fill)
    {
        byte[] output = new byte[length];
        input.AsSpan(0, Math.Min(input.Length, output.Length)).CopyTo(output);
        if (input.Length < output.Length)
        {
            output.AsSpan(input.Length).Fill(fill);
        }
        return output;
    }

    private static byte[] Base64(XElement element, string attribute)
        => Convert.FromBase64String(element.Attribute(attribute)?.Value
            ?? throw new AccessEncryptionException($"Access EncryptionInfo is missing '{attribute}'."));

    private static int Integer(XElement element, string attribute)
    {
        if (!int.TryParse(element.Attribute(attribute)?.Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value))
        {
            throw new AccessEncryptionException($"Access EncryptionInfo has an invalid '{attribute}'.");
        }
        return value;
    }

    private static bool IsAesCbc(XElement element)
        => string.Equals(element.Attribute("cipherAlgorithm")?.Value, "AES",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(element.Attribute("cipherChaining")?.Value, "ChainingModeCBC",
                StringComparison.OrdinalIgnoreCase);
}
