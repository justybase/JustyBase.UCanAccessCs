using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UCanAccess.AccessCrypto;

internal sealed class AgileAccessPageCodec : UCanAccess.File.IAccessPageCodec
{
    private readonly int _pageSize;
    private readonly uint _encodingKey;
    private readonly byte[] _salt;
    private readonly int _blockSize;
    private readonly int _keyBits;
    private readonly HashAlgorithmName _hashAlgorithm;
    private readonly byte[] _masterKey;
    private readonly AccessRootTransform _rootTransform;
    private readonly int _rootMaskOffset;
    private readonly byte[] _rootMask;

    public AgileAccessPageCodec(int pageSize, uint encodingKey, byte[] salt,
        int blockSize, int keyBits, HashAlgorithmName hashAlgorithm,
        byte[] masterKey, AccessRootTransform rootTransform,
        int rootMaskOffset = 0, byte[]? rootMask = null)
    {
        _pageSize = pageSize;
        _encodingKey = encodingKey;
        _salt = salt.ToArray();
        _blockSize = blockSize;
        _keyBits = keyBits;
        _hashAlgorithm = hashAlgorithm;
        _masterKey = masterKey.ToArray();
        if (_keyBits is not (128 or 192 or 256) || _masterKey.Length != _keyBits / 8)
        {
            throw new AccessEncryptionException("The Access page key length is unsupported.");
        }
        _rootTransform = rootTransform;
        _rootMaskOffset = rootMaskOffset;
        _rootMask = rootMask?.ToArray() ?? Array.Empty<byte>();
    }

    public void DecodePage(int pageNumber, ReadOnlySpan<byte> encrypted, Span<byte> plaintext)
    {
        EnsurePage(encrypted, plaintext);
        if (pageNumber == 0)
        {
            encrypted.CopyTo(plaintext);
            TransformRoot(plaintext, encode: false);
            return;
        }
        TransformCipherPage(pageNumber, encrypted, plaintext, decrypt: true);
    }

    public void EncodePage(int pageNumber, ReadOnlySpan<byte> plaintext, Span<byte> encrypted)
    {
        EnsurePage(plaintext, encrypted);
        if (pageNumber == 0)
        {
            plaintext.CopyTo(encrypted);
            TransformRoot(encrypted, encode: true);
            return;
        }
        TransformCipherPage(pageNumber, plaintext, encrypted, decrypt: false);
    }

    private void TransformCipherPage(int pageNumber, ReadOnlySpan<byte> input,
        Span<byte> output, bool decrypt)
    {
        if (_pageSize % _blockSize != 0)
        {
            throw new AccessEncryptionException("The Access page size is not a multiple of its cipher block size.");
        }

        byte[] blockKey = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(blockKey,
            _encodingKey ^ unchecked((uint)pageNumber));
        byte[] ivHash;
        using (HashAlgorithm hash = CreateHash(_hashAlgorithm))
        {
            byte[] ivInput = new byte[_salt.Length + blockKey.Length];
            Buffer.BlockCopy(_salt, 0, ivInput, 0, _salt.Length);
            Buffer.BlockCopy(blockKey, 0, ivInput, _salt.Length, blockKey.Length);
            ivHash = hash.ComputeHash(ivInput);
            CryptographicOperations.ZeroMemory(ivInput);
        }

        byte[] iv = FixToLength(ivHash, _blockSize, 0x36);
        CryptographicOperations.ZeroMemory(ivHash);
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = _masterKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using ICryptoTransform transform = decrypt ? aes.CreateDecryptor() : aes.CreateEncryptor();
            byte[] result = transform.TransformFinalBlock(input.ToArray(), 0, _pageSize);
            result.AsSpan().CopyTo(output);
            CryptographicOperations.ZeroMemory(result);
        }
        catch (CryptographicException ex)
        {
            throw new AccessEncryptionException("The encrypted Access page could not be transformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(blockKey);
        }
    }

    private void TransformRoot(Span<byte> page, bool encode)
    {
        Span<byte> region = page.Slice(_rootMaskOffset, _rootMask.Length);
        if (_rootTransform == AccessRootTransform.Rc4ThenHeaderMask)
        {
            Rc4Transform(region);
        }
        else if (_rootTransform == AccessRootTransform.HeaderMaskThenRc4)
        {
            // PageChannel removes the mask after DecodePage.  Applying it on
            // both sides here gives the same logical root for this historical
            // ordering variant.
            XorHeaderMask(region);
            Rc4Transform(region);
            XorHeaderMask(region);
        }
        else if (_rootTransform == AccessRootTransform.Rc4Only)
        {
            // Current encrypted ACCDB headers are RC4-protected on disk.
            // PageChannel masks the logical root before encoding and
            // unmasks it after decoding, so the two directions have inverse
            // operation order (RC4 and the ordinary Jet mask do not commute).
            if (encode)
            {
                XorHeaderMask(region);
                Rc4Transform(region);
            }
            else
            {
                Rc4Transform(region);
                XorHeaderMask(region);
            }
        }
    }

    private void XorHeaderMask(Span<byte> region)
    {
        for (int i = 0; i < _rootMask.Length && i < region.Length; i++)
        {
            region[i] ^= _rootMask[i];
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

    private void EnsurePage(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length < _pageSize || output.Length < _pageSize)
        {
            throw new ArgumentException("The Access codec requires complete page buffers.");
        }
    }

    private static HashAlgorithm CreateHash(HashAlgorithmName name)
        => name == HashAlgorithmName.SHA512 ? SHA512.Create()
            : name == HashAlgorithmName.SHA256 ? SHA256.Create()
            : name == HashAlgorithmName.SHA1 ? SHA1.Create()
            : throw new AccessEncryptionException($"Unsupported Access page hash algorithm '{name.Name}'.");

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

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_masterKey);
        CryptographicOperations.ZeroMemory(_salt);
    }
}
