using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// ML-KEM-768 from FIPS 203. Zeta powers are 17 raised to the bit-reversed exponent, which is the
/// table in Appendix A. Arithmetic stays in the integers; there is no floating-point rounding.
/// </summary>
internal sealed class ManagedMlKem768 : IMlKem768
{
    internal const int Q = 3329;
    internal const int N = 256;
    internal const int K = 3;
    internal const int EncapsulationKeyLength = 1184;
    internal const int DecapsulationKeyLength = 2400;
    internal const int CiphertextLength = 1088;
    internal const int SharedSecretLength = 32;

    private const int Eta = 2;
    private const int Du = 10;
    private const int Dv = 4;
    private const int Inv128 = 3303;
    private const int PolyBytes12 = 384;
    private const int EncodedU = 320;

    internal static readonly ManagedMlKem768 Instance = new();

    private static readonly short[] Zetas = BuildZetas();
    private static readonly short[] Gammas = BuildGammas();

    private readonly ManagedKeccak _keccak = ManagedKeccak.Instance;

    public void GenerateKeyPair(Span<byte> encapsulationKey, Span<byte> decapsulationKey)
    {
        Span<byte> d = stackalloc byte[32];
        Span<byte> z = stackalloc byte[32];
        RandomNumberGenerator.Fill(d);
        RandomNumberGenerator.Fill(z);
        try
        {
            GenerateKeyPair(encapsulationKey, decapsulationKey, d, z);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
            CryptographicOperations.ZeroMemory(z);
        }
    }

    public void GenerateKeyPair(Span<byte> encapsulationKey, Span<byte> decapsulationKey, ReadOnlySpan<byte> seedD, ReadOnlySpan<byte> seedZ)
    {
        if (encapsulationKey.Length != EncapsulationKeyLength || decapsulationKey.Length != DecapsulationKeyLength || seedD.Length != 32 || seedZ.Length != 32)
        {
            throw new ArgumentException("ML-KEM-768 key generation lengths are fixed.");
        }

        PkeKeyGen(seedD, encapsulationKey, decapsulationKey[..(K * PolyBytes12)]);
        encapsulationKey.CopyTo(decapsulationKey.Slice(K * PolyBytes12, EncapsulationKeyLength));
        _keccak.Sha3_256(encapsulationKey, decapsulationKey.Slice((K * PolyBytes12) + EncapsulationKeyLength, 32));
        seedZ.CopyTo(decapsulationKey[^32..]);
    }

    public bool TryEncapsulate(ReadOnlySpan<byte> encapsulationKey, Span<byte> ciphertext, Span<byte> sharedSecret)
    {
        Span<byte> message = stackalloc byte[32];
        RandomNumberGenerator.Fill(message);
        try
        {
            return TryEncapsulate(encapsulationKey, message, ciphertext, sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    public bool TryEncapsulate(ReadOnlySpan<byte> encapsulationKey, ReadOnlySpan<byte> message, Span<byte> ciphertext, Span<byte> sharedSecret)
    {
        if (encapsulationKey.Length != EncapsulationKeyLength || message.Length != 32 || ciphertext.Length != CiphertextLength || sharedSecret.Length != SharedSecretLength)
        {
            return false;
        }

        if (!ModulusCheck(encapsulationKey))
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[32];
        Span<byte> coins = stackalloc byte[64];
        Span<byte> preimage = stackalloc byte[64];
        try
        {
            _keccak.Sha3_256(encapsulationKey, hash);
            message.CopyTo(preimage);
            hash.CopyTo(preimage[32..]);
            _keccak.Sha3_512(preimage, coins);
            coins[..32].CopyTo(sharedSecret);
            PkeEncrypt(encapsulationKey, message, coins[32..], ciphertext);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(coins);
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    public bool TryDecapsulate(ReadOnlySpan<byte> decapsulationKey, ReadOnlySpan<byte> ciphertext, Span<byte> sharedSecret)
    {
        if (decapsulationKey.Length != DecapsulationKeyLength || ciphertext.Length != CiphertextLength || sharedSecret.Length != SharedSecretLength)
        {
            return false;
        }

        ReadOnlySpan<byte> encryptionKey = decapsulationKey.Slice(K * PolyBytes12, EncapsulationKeyLength);
        ReadOnlySpan<byte> expectedHash = decapsulationKey.Slice((K * PolyBytes12) + EncapsulationKeyLength, 32);
        Span<byte> hash = stackalloc byte[32];
        _keccak.Sha3_256(encryptionKey, hash);
        if (!hash.SequenceEqual(expectedHash) || !ModulusCheck(encryptionKey))
        {
            CryptographicOperations.ZeroMemory(hash);
            return false;
        }

        Span<byte> message = stackalloc byte[32];
        Span<byte> coins = stackalloc byte[64];
        Span<byte> preimage = stackalloc byte[64];
        Span<byte> candidate = stackalloc byte[CiphertextLength];
        Span<byte> rejection = stackalloc byte[32];
        Span<byte> rejectionInput = stackalloc byte[32 + CiphertextLength];
        try
        {
            PkeDecrypt(decapsulationKey[..(K * PolyBytes12)], ciphertext, message);
            message.CopyTo(preimage);
            hash.CopyTo(preimage[32..]);
            _keccak.Sha3_512(preimage, coins);
            PkeEncrypt(encryptionKey, message, coins[32..], candidate);
            decapsulationKey[^32..].CopyTo(rejectionInput);
            ciphertext.CopyTo(rejectionInput[32..]);
            _keccak.Shake256(rejectionInput, rejection);
            uint different = 0;
            for (int i = 0; i < CiphertextLength; i++)
            {
                different |= (uint)(ciphertext[i] ^ candidate[i]);
            }

            uint mask = (uint)((int)-different >> 31);
            for (int i = 0; i < 32; i++)
            {
                sharedSecret[i] = (byte)(coins[i] ^ ((coins[i] ^ rejection[i]) & mask));
            }

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(coins);
            CryptographicOperations.ZeroMemory(preimage);
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(rejection);
            CryptographicOperations.ZeroMemory(rejectionInput);
        }
    }

    private void PkeKeyGen(ReadOnlySpan<byte> seedD, Span<byte> encryptionKey, Span<byte> decryptionKey)
    {
        Span<byte> expanded = stackalloc byte[64];
        Span<byte> domain = stackalloc byte[33];
        seedD.CopyTo(domain);
        domain[32] = K;
        _keccak.Sha3_512(domain, expanded);
        ReadOnlySpan<byte> rho = expanded[..32];
        ReadOnlySpan<byte> sigma = expanded[32..];
        Span<short> matrix = stackalloc short[K * K * N];
        Span<short> secret = stackalloc short[K * N];
        Span<short> noise = stackalloc short[K * N];
        try
        {
            ExpandMatrix(rho, matrix);
            byte nonce = 0;
            for (int i = 0; i < K; i++)
            {
                SamplePolyCbd(sigma, nonce++, secret.Slice(i * N, N));
                Ntt(secret.Slice(i * N, N));
            }

            for (int i = 0; i < K; i++)
            {
                SamplePolyCbd(sigma, nonce++, noise.Slice(i * N, N));
                Ntt(noise.Slice(i * N, N));
            }

            Span<short> product = stackalloc short[N];
            for (int i = 0; i < K; i++)
            {
                product.Clear();
                for (int j = 0; j < K; j++)
                {
                    Accumulate(matrix.Slice(((i * K) + j) * N, N), secret.Slice(j * N, N), product);
                }

                Add(product, noise.Slice(i * N, N));
                ByteEncode(product, 12, encryptionKey.Slice(i * PolyBytes12, PolyBytes12));
                ByteEncode(secret.Slice(i * N, N), 12, decryptionKey.Slice(i * PolyBytes12, PolyBytes12));
            }

            rho.CopyTo(encryptionKey[^32..]);
        }
        finally
        {
            Zero(secret);
            Zero(noise);
            CryptographicOperations.ZeroMemory(expanded);
            CryptographicOperations.ZeroMemory(domain);
        }
    }

    private void PkeEncrypt(ReadOnlySpan<byte> encryptionKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> randomness, Span<byte> ciphertext)
    {
        Span<short> target = stackalloc short[K * N];
        Span<short> matrix = stackalloc short[K * K * N];
        Span<short> vector = stackalloc short[K * N];
        Span<short> error = stackalloc short[K * N];
        Span<short> extra = stackalloc short[N];
        ReadOnlySpan<byte> rho = encryptionKey[^32..];
        ExpandMatrix(rho, matrix);
        byte nonce = 0;
        for (int i = 0; i < K; i++)
        {
            ByteDecode(encryptionKey.Slice(i * PolyBytes12, PolyBytes12), 12, target.Slice(i * N, N));
            SamplePolyCbd(randomness, nonce++, vector.Slice(i * N, N));
            Ntt(vector.Slice(i * N, N));
        }

        for (int i = 0; i < K; i++)
        {
            SamplePolyCbd(randomness, nonce++, error.Slice(i * N, N));
        }

        SamplePolyCbd(randomness, nonce, extra);
        Span<short> accumulated = stackalloc short[N];
        for (int i = 0; i < K; i++)
        {
            accumulated.Clear();
            for (int j = 0; j < K; j++)
            {
                Accumulate(matrix.Slice(((j * K) + i) * N, N), vector.Slice(j * N, N), accumulated);
            }

            InverseNtt(accumulated);
            Add(accumulated, error.Slice(i * N, N));
            CompressEncode(accumulated, Du, ciphertext.Slice(i * EncodedU, EncodedU));
        }

        accumulated.Clear();
        for (int j = 0; j < K; j++)
        {
            Accumulate(target.Slice(j * N, N), vector.Slice(j * N, N), accumulated);
        }

        InverseNtt(accumulated);
        Add(accumulated, extra);
        AddMessage(accumulated, message);
        CompressEncode(accumulated, Dv, ciphertext[(K * EncodedU)..]);
        Zero(vector);
        Zero(error);
        Zero(extra);
        Zero(accumulated);
    }

    private void PkeDecrypt(ReadOnlySpan<byte> decryptionKey, ReadOnlySpan<byte> ciphertext, Span<byte> message)
    {
        Span<short> secret = stackalloc short[K * N];
        Span<short> u = stackalloc short[K * N];
        Span<short> v = stackalloc short[N];
        for (int i = 0; i < K; i++)
        {
            ByteDecode(decryptionKey.Slice(i * PolyBytes12, PolyBytes12), 12, secret.Slice(i * N, N));
            DecompressDecode(ciphertext.Slice(i * EncodedU, EncodedU), Du, u.Slice(i * N, N));
            Ntt(u.Slice(i * N, N));
        }

        DecompressDecode(ciphertext[(K * EncodedU)..], Dv, v);
        Span<short> accumulated = stackalloc short[N];
        for (int j = 0; j < K; j++)
        {
            Accumulate(secret.Slice(j * N, N), u.Slice(j * N, N), accumulated);
        }

        InverseNtt(accumulated);
        Sub(v, accumulated);
        CompressEncodeMessage(v, message);
        Zero(secret);
        Zero(u);
        Zero(v);
        Zero(accumulated);
    }

    private void ExpandMatrix(ReadOnlySpan<byte> rho, Span<short> matrix)
    {
        Span<byte> seed = stackalloc byte[34];
        Span<byte> stream = stackalloc byte[1008];
        rho.CopyTo(seed);
        for (int i = 0; i < K; i++)
        {
            for (int j = 0; j < K; j++)
            {
                seed[32] = (byte)j;
                seed[33] = (byte)i;
                _keccak.Shake128(seed, stream);
                SampleNtt(stream, matrix.Slice(((i * K) + j) * N, N));
            }
        }
    }

    private void SamplePolyCbd(ReadOnlySpan<byte> sigma, byte nonce, Span<short> polynomial)
    {
        Span<byte> input = stackalloc byte[33];
        Span<byte> block = stackalloc byte[64 * Eta];
        sigma.CopyTo(input);
        input[32] = nonce;
        _keccak.Shake256(input, block);
        for (int i = 0; i < N; i++)
        {
            int x = 0;
            int y = 0;
            for (int bit = 0; bit < Eta; bit++)
            {
                x += ReadBit(block, (2 * Eta * i) + bit);
                y += ReadBit(block, (2 * Eta * i) + Eta + bit);
            }

            polynomial[i] = (short)Mod(x - y);
        }
    }

    private static void SampleNtt(ReadOnlySpan<byte> stream, Span<short> polynomial)
    {
        int filled = 0;
        int offset = 0;
        while (filled < N)
        {
            if (offset + 3 > stream.Length)
            {
                throw new CryptographicException("SampleNTT exceeded its XOF bound.");
            }

            int d1 = stream[offset] + (256 * (stream[offset + 1] & 0x0F));
            int d2 = (stream[offset + 1] >> 4) + (16 * stream[offset + 2]);
            offset += 3;
            if (d1 < Q)
            {
                polynomial[filled++] = (short)d1;
            }

            if (d2 < Q && filled < N)
            {
                polynomial[filled++] = (short)d2;
            }
        }
    }

    private static void Ntt(Span<short> polynomial)
    {
        int k = 1;
        for (int length = 128; length >= 2; length >>= 1)
        {
            for (int start = 0; start < N; start += 2 * length)
            {
                short zeta = Zetas[k++];
                for (int j = start; j < start + length; j++)
                {
                    int t = Mul(zeta, polynomial[j + length]);
                    polynomial[j + length] = (short)Mod(polynomial[j] - t);
                    polynomial[j] = (short)Mod(polynomial[j] + t);
                }
            }
        }
    }

    private static void InverseNtt(Span<short> polynomial)
    {
        int k = 127;
        for (int length = 2; length <= 128; length <<= 1)
        {
            for (int start = 0; start < N; start += 2 * length)
            {
                short zeta = Zetas[k--];
                for (int j = start; j < start + length; j++)
                {
                    int t = polynomial[j];
                    polynomial[j] = (short)Mod(t + polynomial[j + length]);
                    polynomial[j + length] = (short)Mul(zeta, Mod(polynomial[j + length] - t));
                }
            }
        }

        for (int i = 0; i < N; i++)
        {
            polynomial[i] = (short)Mul(polynomial[i], Inv128);
        }
    }

    private static void Accumulate(ReadOnlySpan<short> left, ReadOnlySpan<short> right, Span<short> destination)
    {
        for (int i = 0; i < 128; i++)
        {
            int gamma = Gammas[i];
            int a0 = left[2 * i];
            int a1 = left[(2 * i) + 1];
            int b0 = right[2 * i];
            int b1 = right[(2 * i) + 1];
            int r0 = Mod(((long)a0 * b0) + ((long)Mul(a1, b1) * gamma));
            int r1 = Mod(((long)a0 * b1) + ((long)a1 * b0));
            destination[2 * i] = (short)Mod(destination[2 * i] + r0);
            destination[(2 * i) + 1] = (short)Mod(destination[(2 * i) + 1] + r1);
        }
    }

    private static void Add(Span<short> left, ReadOnlySpan<short> right)
    {
        for (int i = 0; i < N; i++)
        {
            left[i] = (short)Mod(left[i] + right[i]);
        }
    }

    private static void Sub(Span<short> left, ReadOnlySpan<short> right)
    {
        for (int i = 0; i < N; i++)
        {
            left[i] = (short)Mod(left[i] - right[i]);
        }
    }

    private static void AddMessage(Span<short> polynomial, ReadOnlySpan<byte> message)
    {
        for (int i = 0; i < N; i++)
        {
            if (ReadBit(message, i) == 1)
            {
                polynomial[i] = (short)Mod(polynomial[i] + ((Q + 1) / 2));
            }
        }
    }

    private bool ModulusCheck(ReadOnlySpan<byte> encapsulationKey)
    {
        Span<short> polynomial = stackalloc short[N];
        Span<byte> encoded = stackalloc byte[PolyBytes12];
        for (int i = 0; i < K; i++)
        {
            ReadOnlySpan<byte> slice = encapsulationKey.Slice(i * PolyBytes12, PolyBytes12);
            ByteDecode(slice, 12, polynomial);
            ByteEncode(polynomial, 12, encoded);
            if (!encoded.SequenceEqual(slice))
            {
                return false;
            }
        }

        return true;
    }

    private static void CompressEncode(ReadOnlySpan<short> polynomial, int bits, Span<byte> destination)
    {
        Span<short> compressed = stackalloc short[N];
        for (int i = 0; i < N; i++)
        {
            compressed[i] = (short)((((long)polynomial[i] << (bits + 1)) + Q) / (2 * Q) & ((1 << bits) - 1));
        }

        ByteEncode(compressed, bits, destination);
    }

    private static void DecompressDecode(ReadOnlySpan<byte> source, int bits, Span<short> polynomial)
    {
        ByteDecode(source, bits, polynomial);
        int half = 1 << (bits - 1);
        for (int i = 0; i < N; i++)
        {
            polynomial[i] = (short)((((long)polynomial[i] * Q) + half) >> bits);
        }
    }

    private static void CompressEncodeMessage(ReadOnlySpan<short> polynomial, Span<byte> message)
    {
        message.Clear();
        for (int i = 0; i < N; i++)
        {
            int bit = (int)((((long)polynomial[i] << 2) + Q) / (2 * Q) & 1);
            message[i >> 3] |= (byte)(bit << (i & 7));
        }
    }

    private static void ByteEncode(ReadOnlySpan<short> polynomial, int bits, Span<byte> destination)
    {
        destination.Clear();
        int acc = 0;
        int filled = 0;
        int position = 0;
        for (int i = 0; i < N; i++)
        {
            acc |= polynomial[i] << filled;
            filled += bits;
            while (filled >= 8)
            {
                destination[position++] = (byte)acc;
                acc >>= 8;
                filled -= 8;
            }
        }
    }

    private static void ByteDecode(ReadOnlySpan<byte> source, int bits, Span<short> polynomial)
    {
        int acc = 0;
        int filled = 0;
        int position = 0;
        int mask = (1 << bits) - 1;
        for (int i = 0; i < N; i++)
        {
            while (filled < bits)
            {
                acc |= source[position++] << filled;
                filled += 8;
            }

            int value = acc & mask;
            polynomial[i] = (short)(bits == 12 ? value % Q : value);
            acc >>= bits;
            filled -= bits;
        }
    }

    private static void Zero(Span<short> values)
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(values));
    }

    private static int ReadBit(ReadOnlySpan<byte> bytes, int index)
    {
        return (bytes[index >> 3] >> (index & 7)) & 1;
    }

    private static int Mul(int left, int right)
    {
        return Mod((long)left * right);
    }

    private static int Mod(long value)
    {
        int reduced = (int)(value % Q);
        return reduced < 0 ? reduced + Q : reduced;
    }

    private static short[] BuildZetas()
    {
        short[] zetas = new short[128];
        for (int i = 0; i < 128; i++)
        {
            zetas[i] = (short)ModPow(17, BitRev7(i));
        }

        return zetas;
    }

    private static short[] BuildGammas()
    {
        short[] gammas = new short[128];
        for (int i = 0; i < 128; i++)
        {
            gammas[i] = (short)ModPow(17, (2 * BitRev7(i)) + 1);
        }

        return gammas;
    }

    private static int BitRev7(int value)
    {
        int reversed = 0;
        for (int bit = 0; bit < 7; bit++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }

    private static int ModPow(int value, int exponent)
    {
        int result = 1;
        int baseValue = value;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
            {
                result = (int)((long)result * baseValue % Q);
            }

            baseValue = (int)((long)baseValue * baseValue % Q);
            exponent >>= 1;
        }

        return result;
    }
}
