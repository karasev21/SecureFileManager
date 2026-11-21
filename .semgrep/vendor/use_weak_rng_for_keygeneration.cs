using System.Security.Cryptography;

public class WeakKeyExamples
{
    public void Unsafe()
    {
        System.Random rng = new System.Random();
        byte[] key = new byte[32];
        rng.NextBytes(key);
        // ruleid: use_weak_rng_for_keygeneration
        using var cipher = new AesGcm(key, 16);
    }

    public void Safe()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        // ok: use_weak_rng_for_keygeneration
        using var cipher = new AesGcm(key, 16);
    }
}
