# Pinned upstream Semgrep rules

These files are copied from the official [semgrep/semgrep-rules repository](https://github.com/semgrep/semgrep-rules) at commit `a5ad1714f00c9f7fde980cab0ea1d6dc6d414fd2`.
Rule IDs, patterns, severity, messages and metadata are unchanged. Only provenance comments were added. The adjacent C# fixtures are project-owned minimal regression examples.

| Rule | Original source | Scope |
| --- | --- | --- |
| `use_deprecated_cipher_algorithm` | [csharp/dotnet/security/use_deprecated_cipher_algorithm.yaml](https://github.com/semgrep/semgrep-rules/blob/a5ad1714f00c9f7fde980cab0ea1d6dc6d414fd2/csharp/dotnet/security/use_deprecated_cipher_algorithm.yaml) | `DES.Create(...)` and `RC2.Create(...)` |
| `use_weak_rng_for_keygeneration` | [csharp/dotnet/security/use_weak_rng_for_keygeneration.yaml](https://github.com/semgrep/semgrep-rules/blob/a5ad1714f00c9f7fde980cab0ea1d6dc6d414fd2/csharp/dotnet/security/use_weak_rng_for_keygeneration.yaml) | `System.Random.NextBytes` flowing into cryptographic constructors |

License: **Semgrep Rules License v1.0**. The upstream [LICENSE](https://github.com/semgrep/semgrep-rules/blob/a5ad1714f00c9f7fde980cab0ea1d6dc6d414fd2/LICENSE) is retained as `LICENSE`; its terms are published at [semgrep.dev/legal/rules-license](https://semgrep.dev/legal/rules-license).

The weak RNG fixture exercises the `AesGcm` constructor in Semgrep CE. The upstream typed `cipher.Key` assignment alternative is preserved, but its upstream examples are marked `deeptodoruleid`; this alternative is not claimed as verified CE coverage. The upstream ECB rule is deliberately absent: the project supplies a separately tested narrow ECB rule.

From the repository root, verify these fixtures using the same pinned Semgrep version as CI:

```sh
semgrep --validate --config .semgrep/vendor
semgrep --test .semgrep/vendor
```
