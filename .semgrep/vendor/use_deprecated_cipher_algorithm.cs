using System.Security.Cryptography;

public class DeprecatedCipherExamples
{
    public void Unsafe()
    {
        // ruleid: use_deprecated_cipher_algorithm
        using var des = DES.Create();
        // ruleid: use_deprecated_cipher_algorithm
        using var rc2 = RC2.Create();
    }

    public void Safe()
    {
        // ok: use_deprecated_cipher_algorithm
        using var aes = Aes.Create();
    }
}
