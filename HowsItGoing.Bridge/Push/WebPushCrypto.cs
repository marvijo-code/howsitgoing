using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HowsItGoing.Bridge.Push;

/// <summary>A VAPID (RFC 8292) application-server key pair, both halves base64url without padding.</summary>
/// <param name="PublicKey">The uncompressed P-256 point, 65 bytes. This is what the browser passes to <c>PushManager.subscribe</c>.</param>
/// <param name="PrivateKey">The 32-byte private scalar. Never leaves the bridge.</param>
public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

/// <summary>
/// The Web Push wire format, implemented on BCL primitives only so the bridge picks up no new
/// dependency for a feature this small.
///
/// Three specs stack up here:
/// <list type="bullet">
///   <item>RFC 8291 derives a content-encryption key from an ECDH exchange with the browser.</item>
///   <item>RFC 8188 ("aes128gcm") frames that key's output into the request body.</item>
///   <item>RFC 8292 (VAPID) proves to the push service which application server is sending.</item>
/// </list>
///
/// <see cref="Encrypt"/> is verified against the RFC 8291 section 5 test vector in the bridge tests -
/// hand-rolled crypto is only defensible because the spec ships a vector to check it against.
/// </summary>
public static class WebPushCrypto
{
    public const int SaltLength = 16;
    public const int PublicKeyLength = 65;
    public const int AuthSecretLength = 16;
    public const int DefaultRecordSize = 4096;

    /// <summary>Bytes AES-GCM appends beyond the plaintext, plus the one-byte record delimiter.</summary>
    private const int RecordOverhead = 17;

    private static readonly byte[] KeyInfoPrefix = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] ContentEncryptionKeyInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    public static VapidKeyPair GenerateVapidKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        return new VapidKeyPair(
            Base64Url.EncodeToString(ToUncompressedPoint(parameters)),
            Base64Url.EncodeToString(parameters.D!));
    }

    /// <summary>
    /// Builds the <c>Authorization</c> header the push service checks. The audience is the endpoint's
    /// origin only - including the path would make every subscription need its own token.
    /// </summary>
    public static string CreateVapidAuthorization(Uri endpoint, string subject, VapidKeyPair keys, DateTimeOffset expiresAt)
    {
        var header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);
        // Lower-case member names on purpose: these are the JWT claim names, and the default
        // serializer options would otherwise emit "Aud"/"Exp"/"Sub" and fail validation.
        var claims = JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = expiresAt.ToUnixTimeSeconds(),
            sub = subject
        });
        var signingInput = $"{header}.{Base64Url.EncodeToString(claims)}";

        using var ecdsa = ECDsa.Create(ToEcParameters(keys));
        // JWS wants the raw r||s pair, which is what SignData produces by default.
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

        return $"vapid t={signingInput}.{Base64Url.EncodeToString(signature)}, k={keys.PublicKey}";
    }

    /// <summary>
    /// Encrypts <paramref name="payload"/> into a complete aes128gcm request body.
    /// </summary>
    /// <param name="serverKey">
    /// The ephemeral application-server key. Callers pass a freshly generated key per message; the
    /// tests pass the fixed key from the RFC so the output can be compared byte for byte.
    /// </param>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> salt,
        ECDiffieHellman serverKey,
        int recordSize = DefaultRecordSize)
    {
        if (userAgentPublicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException($"The subscription p256dh key must be {PublicKeyLength} bytes.", nameof(userAgentPublicKey));
        }

        if (authSecret.Length != AuthSecretLength)
        {
            throw new ArgumentException($"The subscription auth secret must be {AuthSecretLength} bytes.", nameof(authSecret));
        }

        if (salt.Length != SaltLength)
        {
            throw new ArgumentException($"The salt must be {SaltLength} bytes.", nameof(salt));
        }

        // Everything is sent as a single record, so the payload has to fit inside one.
        if (payload.Length + RecordOverhead > recordSize)
        {
            throw new ArgumentException(
                $"A {payload.Length}-byte payload does not fit in a {recordSize}-byte record.",
                nameof(payload));
        }

        var serverPublicKey = ToUncompressedPoint(serverKey.ExportParameters(includePrivateParameters: false));

        using var userAgentKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = userAgentPublicKey[1..33].ToArray(),
                Y = userAgentPublicKey[33..65].ToArray()
            }
        });

        var sharedSecret = serverKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);

        // RFC 8291 mixes the browser's auth secret into the shared secret before the RFC 8188 schedule
        // begins, which is what stops the push service from reading the payload.
        var authPrk = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, authSecret.ToArray());
        var keyInfo = Concat(KeyInfoPrefix, userAgentPublicKey, serverPublicKey);
        var inputKeyingMaterial = HKDF.Expand(HashAlgorithmName.SHA256, authPrk, 32, keyInfo);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, inputKeyingMaterial, salt.ToArray());
        var contentEncryptionKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, ContentEncryptionKeyInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // A record is the plaintext followed by a padding delimiter; 0x02 marks the last record.
        var plaintext = new byte[payload.Length + 1];
        payload.CopyTo(plaintext);
        plaintext[^1] = 0x02;

        var ciphertext = new byte[plaintext.Length + AuthSecretLength];
        using (var aes = new AesGcm(contentEncryptionKey, AuthSecretLength))
        {
            aes.Encrypt(
                nonce,
                plaintext,
                ciphertext.AsSpan(0, plaintext.Length),
                ciphertext.AsSpan(plaintext.Length));
        }

        // RFC 8188 header: salt | record size | key id length | key id (the server public key).
        var body = new byte[SaltLength + 4 + 1 + serverPublicKey.Length + ciphertext.Length];
        salt.CopyTo(body);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(SaltLength, 4), (uint)recordSize);
        body[SaltLength + 4] = (byte)serverPublicKey.Length;
        serverPublicKey.CopyTo(body.AsSpan(SaltLength + 5));
        ciphertext.CopyTo(body.AsSpan(SaltLength + 5 + serverPublicKey.Length));

        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(contentEncryptionKey);
        CryptographicOperations.ZeroMemory(plaintext);

        return body;
    }

    public static ECDiffieHellman CreateEphemeralServerKey() =>
        ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Rebuilds the ECDH key pair the RFC test vector fixes, so the tests can reproduce it.</summary>
    public static ECDiffieHellman CreateServerKey(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey.ToArray(),
            Q = new ECPoint
            {
                X = publicKey[1..33].ToArray(),
                Y = publicKey[33..65].ToArray()
            }
        });

    public static byte[] DecodeKey(string base64Url, int expectedLength, string name)
    {
        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(base64Url.AsSpan());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"{name} is not valid base64url.", name, ex);
        }

        return decoded.Length == expectedLength
            ? decoded
            : throw new ArgumentException($"{name} must decode to {expectedLength} bytes but was {decoded.Length}.", name);
    }

    private static ECParameters ToEcParameters(VapidKeyPair keys)
    {
        var publicKey = DecodeKey(keys.PublicKey, PublicKeyLength, nameof(keys.PublicKey));
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = DecodeKey(keys.PrivateKey, 32, nameof(keys.PrivateKey)),
            Q = new ECPoint
            {
                X = publicKey[1..33],
                Y = publicKey[33..65]
            }
        };
    }

    private static byte[] ToUncompressedPoint(ECParameters parameters) =>
        [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];

    private static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third)
    {
        var result = new byte[first.Length + second.Length + third.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        third.CopyTo(result.AsSpan(first.Length + second.Length));
        return result;
    }
}
