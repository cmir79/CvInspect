#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>init 접근자/record 컴파일용 폴리필 — netstandard2.1 대상에서만 포함된다.
/// 코어에도 같은 폴리필이 있지만 internal 이라 어셈블리를 넘지 않으므로 여기에도 둔다.</summary>
internal static class IsExternalInit;
#endif
