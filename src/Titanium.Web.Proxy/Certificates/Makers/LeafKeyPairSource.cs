using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Security;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Supplies the key pair for each generated leaf certificate.
///     <para>
///         An RSA-2048 key pair costs hundreds of milliseconds of CPU to produce (prime search), and one
///         is needed for every not-yet-cached hostname. Generating it inline puts that whole cost on the
///         CONNECT that happens to reach a host first, and a page pulling resources from many hosts starts
///         that many generations at once - which, being CPU-bound, then starve each other and inflate
///         every one of them several-fold. So RSA key pairs are taken from a small buffer that a
///         background task keeps topped up: a key produced while the proxy was idle is handed over
///         immediately, and only a burst longer than the buffer waits on generation at all.
///     </para>
///     <para>
///         P-256 key pairs are cheap enough (a single scalar multiplication) that buffering them would
///         cost more than it saves, so they are generated on demand.
///     </para>
///     <para>
///         Either way each certificate gets its own distinct key. That is the difference from
///         <see cref="CertificateEngine.BouncyCastleFast" />, which reaches similar speed by sharing one
///         key pair across every host.
///     </para>
/// </summary>
internal static class LeafKeyPairSource
{
    /// <summary>
    ///     The RSA strength that is buffered. Leaf and root generation both use this size, so a second
    ///     bucket would add complexity for no benefit; other strengths are generated on demand.
    /// </summary>
    internal const int BufferedRsaKeyStrength = 2048;

    /// <summary>
    ///     Default number of ready-to-use RSA key pairs to keep. Sized to cover the burst of distinct
    ///     hosts a typical page load contacts, while keeping the idle CPU cost of staying topped up
    ///     bounded.
    /// </summary>
    internal const int DefaultRsaBufferCapacity = 8;

    /// <summary>
    ///     Upper bound for <see cref="RsaBufferCapacity" /> so a misconfigured value cannot pin
    ///     unbounded memory and CPU into key generation.
    /// </summary>
    internal const int MaxRsaBufferCapacity = 256;

    /// <summary>
    ///     Background RSA generators allowed at once. Deliberately well below the core count: refilling
    ///     is never urgent, and the proxy's own request handling must not be crowded out by it.
    /// </summary>
    private const int MaxConcurrentGenerators = 2;

    private static readonly ConcurrentBag<AsymmetricCipherKeyPair> RsaBuffer = new();

    private static int rsaBufferCapacity = DefaultRsaBufferCapacity;
    private static int buffered;
    private static int generatorsRunning;

    /// <summary>
    ///     How many RSA-2048 key pairs to keep ready. Process-wide. 0 disables buffering.
    /// </summary>
    internal static int RsaBufferCapacity
    {
        get => Volatile.Read(ref rsaBufferCapacity);
        set
        {
            if (value < 0 || value > MaxRsaBufferCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Leaf RSA key-pair buffer size must be between 0 and {MaxRsaBufferCapacity}.");
            }

            Volatile.Write(ref rsaBufferCapacity, value);
            if (value > 0) RequestRefill();
        }
    }

    /// <summary>
    ///     Produces a key pair for one certificate, from the buffer when a suitable one is ready.
    /// </summary>
    internal static AsymmetricCipherKeyPair Rent(CertificateKeyAlgorithm algorithm, int rsaKeyStrength)
    {
        if (algorithm == CertificateKeyAlgorithm.EcdsaP256) return GenerateEcdsaP256();

        if (rsaKeyStrength != BufferedRsaKeyStrength) return GenerateRsa(rsaKeyStrength);

        // A refill is requested whether or not this call was served from the buffer: the first
        // certificate of a session necessarily misses, and that miss is what should start filling.
        if (RsaBuffer.TryTake(out var pooled))
        {
            Interlocked.Decrement(ref buffered);
            RequestRefill();
            return pooled;
        }

        RequestRefill();
        return GenerateRsa(rsaKeyStrength);
    }

    internal static AsymmetricCipherKeyPair GenerateRsa(int keyStrength)
    {
        var secureRandom = new SecureRandom(new CryptoApiRandomGenerator());
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(secureRandom, keyStrength));
        return generator.GenerateKeyPair();
    }

    internal static AsymmetricCipherKeyPair GenerateEcdsaP256()
    {
        var secureRandom = new SecureRandom(new CryptoApiRandomGenerator());
        var curve = ECNamedCurveTable.GetByOid(X9ObjectIdentifiers.Prime256v1);

        // Named rather than plain ECDomainParameters so the private key serialises with the curve's OID.
        // Encoding the curve explicitly instead is legal but unimportable: Windows CNG rejects PKCS#12
        // and PKCS#8 EC keys that spell their parameters out rather than naming a known curve.
        var domainParameters = new ECNamedDomainParameters(X9ObjectIdentifiers.Prime256v1,
            curve.Curve, curve.G, curve.N, curve.H, curve.GetSeed());

        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(domainParameters, secureRandom));
        return generator.GenerateKeyPair();
    }

    private static void RequestRefill()
    {
        while (true)
        {
            var capacity = RsaBufferCapacity;
            if (capacity <= 0) return;

            var running = Volatile.Read(ref generatorsRunning);
            if (running >= MaxConcurrentGenerators) return;

            // Each running generator keeps producing until the buffer is full, so one already in
            // flight is enough to close any gap; count it as covering one slot to avoid piling on.
            if (Volatile.Read(ref buffered) + running >= capacity) return;

            if (Interlocked.CompareExchange(ref generatorsRunning, running + 1, running) == running) break;
        }

        _ = Task.Run(static () =>
        {
            try
            {
                while (true)
                {
                    var capacity = RsaBufferCapacity;
                    if (capacity <= 0 || Volatile.Read(ref buffered) >= capacity) break;

                    RsaBuffer.Add(GenerateRsa(BufferedRsaKeyStrength));
                    Interlocked.Increment(ref buffered);
                }
            }
            catch (Exception)
            {
                // A failed refill must not surface on this thread - the next Rent falls back to
                // generating inline, which reports failure on a path that can handle it.
            }
            finally
            {
                Interlocked.Decrement(ref generatorsRunning);
            }
        });
    }
}
