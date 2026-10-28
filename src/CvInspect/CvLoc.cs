using System.Text.Json;

namespace CvInspect;

/// <summary>
/// 라이브러리 번역 접근점 — 키 형식은 <c>"scope:key"</c> (라이브러리 자체 키는 <c>cv:</c> 스코프).
///
/// 해석 순서(고정):
///   1. <see cref="Resolver"/> — 호스트가 주입한 번역기. null 반환 = 모르는 키.
///   2. 내장 사전 — 어셈블리 내장 리소스를 기반 표로 적재하고, 실행 폴더
///      <c>Assets/lang/cv.{culture}.json</c>(현장 편집용)이 있으면 <b>키 단위로 덮어쓴다</b> —
///      일부 키만 담은 편집 파일이 내장 표를 통째로 가리지 않는다.
///      문화권은 정규화 태그 → 상위 언어(예: ko-KR→ko) → <c>en</c> 순으로 조회한다.
///   3. 키 원문(스코프 제거) 반환 + <see cref="MissingKey"/> 1회 통지.
///
/// Resolver 가 모르는 키에 null 대신 대체 문자열을 돌려주면 내장 폴백과 누락 진단이 조용히 죽는다.
/// 값은 캐시하지 않는다 — 언어 전환은 다음 조회부터 반영된다. 조회가 그릴 때만 일어나는 화면(무이미지 안내처럼 다음 그리기가
/// 언제 올지 모르는 것)은 그것만으로는 옛 언어로 남으므로, <see cref="Changed"/> 를 듣고 다시 그린다.
/// 적재는 리더 기반(<see cref="JsonDocument"/>)이라 리플렉션 직렬화가 없는 런타임(IL2CPP 등)에서도 동작한다.
/// </summary>
public static class CvLoc
{
    private const string NeutralCulture = "en";
    private const string LibScope = "cv";

    private static readonly object _sync = new();
    private static readonly Dictionary<string, Dictionary<string, string>?> _tables = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    private static string _culture = NeutralCulture;
    private static bool _cultureSet;        // 호스트가 Culture 를 한 번이라도 골랐는가 — 기본값과 명시된 같은 값을 가른다
    private static bool _cultureNoticed;    // 안 고른 채 첫 조회가 왔을 때의 알림을 냈는가
    private static Func<string, string?>? _resolver;

    // 표 적재 중 로그 sink 가 CvLoc.T 를 되부르는 재진입 차단 — 없으면 미적재 문화권에서 무한 재귀.
    [ThreadStatic] private static bool _loading;

    /// <summary>호스트 번역 주입 지점 — <c>"scope:key"</c> 전체를 받아 번역을 반환, 모르는 키는 null.
    /// 교체하면 누락 통지 이력을 비워 새 Resolver 기준으로 다시 1회씩 경고한다.</summary>
    public static Func<string, string?>? Resolver
    {
        get => _resolver;
        set
        {
            lock (_sync)
            {
                _resolver = value;
                _missingLogged.Clear();
            }
            RaiseChanged();
        }
    }

    /// <summary>내장 사전 선택 문화권 (기본 "en"). "ko-KR" 같은 지역 태그는 상위 언어로 접어 조회한다.
    /// Resolver 는 이 값과 무관하게 자기 문화를 따른다.</summary>
    public static string Culture
    {
        get { lock (_sync) return _culture; }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lock (_sync)
            {
                _cultureSet = true;   // 같은 값을 다시 골라도 "골랐다" 는 사실은 남는다 — 명시된 en 은 기본 en 과 다르다
                if (string.Equals(_culture, value, StringComparison.OrdinalIgnoreCase)) return;
                _culture = value;
                _missingLogged.Clear();
            }
            RaiseChanged();   // 값이 실제로 바뀐 때만 — 같은 값을 다시 고르면 위에서 돌아간다
        }
    }

    /// <summary>Resolver 와 내장 사전 어디에도 없는 키를 1회씩 통지. 구독자가 없으면 <see cref="CvLog"/> 로 흐른다.</summary>
    public static event Action<string>? MissingKey;

    /// <summary><see cref="T"/> 가 돌려줄 값이 바뀌었을 수 있다는 통지 — <see cref="Culture"/> 가 다른 값으로 바뀔 때,
    /// <see cref="Resolver"/> 를 대입할 때, <see cref="Reload"/> 때 분다. <b>바꾼 스레드에서 락 밖으로</b> 불리니 화면은 자기 UI 스레드로
    /// 넘겨 다시 그린다. 구독자 하나가 던져도 나머지는 받고, 예외는 경고 로그로 남을 뿐 문화권을 바꾼 호출자에게 가지 않는다.
    /// 정적 이벤트라 구독자를 붙잡는다 — 화면 컨트롤은 올라와 있는 동안만 건다. 처리기 안에서 다시 바꾼 것(예: 처리기가 <see cref="Reload"/> 를
    /// 부름)은 따로 통지하지 않는다 — 되불림이 끝없이 이어지지 않게 막는 것이고, 바깥 통지를 받는 구독자는 그 뒤 최신 값을 읽는다.</summary>
    public static event Action? Changed;

    /// <summary>내장 사전 캐시 파기 — 실행 폴더의 json 을 고친 뒤 재적재용.</summary>
    public static void Reload()
    {
        lock (_sync)
        {
            _tables.Clear();
            _missingLogged.Clear();
        }
        RaiseChanged();
    }

    // 통지 중 재진입 차단 — 구독자가 처리기 안에서 Reload·Resolver·Culture 를 다시 건드리면 통지가 끝없이 되불려 스택이 넘친다(잡을 수 없는
    // 예외라 프로세스가 죽는다). 안쪽 변경은 통지하지 않는다 — 바깥 통지가 아직 돌고 있고, 구독자는 그 뒤 T 를 다시 읽으니 최신 값을 본다.
    [ThreadStatic] private static bool _raising;

    private static void RaiseChanged()
    {
        if (_raising || Changed is not { } handler) return;
        _raising = true;
        try
        {
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception ex)
                {
                    CvLog.Publish(CvLogLevel.Warning, nameof(CvLoc), "a CvLoc.Changed subscriber threw; the remaining subscribers were still notified.", ex);
                }
            }
        }
        finally { _raising = false; }
    }

    /// <summary>호스트가 <see cref="Culture"/> 를 명시했는가. 기본 "en" 은 "안 골랐다" 와 "en 을 골랐다" 를 구분하지
    /// 못한다 — 한 소비자가 기본값인 줄 모른 채 영어 화면으로 이틀을 돌았고, 키가 다 있어 <see cref="MissingKey"/> 는
    /// 울리지 않았다. 기동 검사에서 단언한다. 같은 값을 다시 골라도 "골랐다" 는 사실은 남는다.</summary>
    public static bool IsCultureSet { get { lock (_sync) return _cultureSet; } }

    /// <summary>문화권을 한 번도 고르지 않은 채 첫 조회가 오면 진단 추적에 한 번만 알린다. <see cref="Resolver"/> 가 꽂혀
    /// 있으면 번역은 호스트가 맡은 것이므로 알리지 않는다. 예외도 로그도 아니다 — 이 라이브러리의 로그 자체가 배선을
    /// 기다리는 쪽이라 진단 추적만 쓴다.</summary>
    private static void NoticeDefaultCultureOnce()
    {
        lock (_sync)
        {
            if (_cultureSet || _cultureNoticed || _resolver is not null) return;
            _cultureNoticed = true;
        }
        System.Diagnostics.Trace.TraceWarning(
            "CvInspect: CvLoc.Culture was never set, so translations fall back to 'en'. " +
            "Set CvLoc.Culture at startup (set 'en' explicitly to keep English and silence this) and assert CvLoc.IsCultureSet.");
    }

    /// <summary>번역 조회. 실패 시 키 원문(스코프 프리픽스 제거)을 반환한다 — 예외를 내지 않는다.</summary>
    public static string T(string scopedKey)
    {
        if (string.IsNullOrEmpty(scopedKey)) return scopedKey ?? string.Empty;
        NoticeDefaultCultureOnce();

        var custom = Resolver?.Invoke(scopedKey);
        if (custom is not null) return custom;

        var idx = scopedKey.IndexOf(':');
        if (idx <= 0)
        {
            WarnOnce(scopedKey, $"missing scope prefix: {scopedKey}");
            return scopedKey;
        }

        var scope = scopedKey.Substring(0, idx);
        var key = scopedKey.Substring(idx + 1);
        var cul = Culture;

        if (string.Equals(scope, LibScope, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in Candidates(cul))
            {
                var val = Lookup(candidate, key);
                if (val is not null) return val;
            }
        }

        WarnOnce($"{scope}:{key}@{cul}", $"missing key: scope={scope}, key={key}, culture={cul}");
        return key;
    }

    /// <summary>조회 후보 문화권 — 소문자 정규화 태그 → 상위 언어(ko-KR→ko) → 중립(en). 중복은 건너뛴다.</summary>
    private static IEnumerable<string> Candidates(string culture)
    {
        var full = culture.Trim().ToLowerInvariant();
        yield return full;

        var dash = full.IndexOf('-');
        var parent = dash > 0 ? full.Substring(0, dash) : full;
        if (parent.Length > 0 && parent != full) yield return parent;
        if (parent != NeutralCulture && full != NeutralCulture) yield return NeutralCulture;
    }

    private static string? Lookup(string culture, string key)
    {
        Dictionary<string, string>? table;
        bool cached;
        lock (_sync) cached = _tables.TryGetValue(culture, out table);

        if (!cached)
        {
            if (_loading) return null;   // 재진입(적재 중 로그 경유) — 캐시 오염 없이 빠진다

            Dictionary<string, string>? loaded;
            _loading = true;
            try { loaded = LoadTable(culture); }   // 파일 I/O 와 로그 콜백은 락 밖에서 — 락 순서 역전 차단
            finally { _loading = false; }

            lock (_sync)
            {
                if (!_tables.TryGetValue(culture, out table))
                {
                    _tables[culture] = loaded;
                    table = loaded;
                }
            }
        }

        return table is not null && table.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>내장 리소스를 기반 표로 적재한 뒤, 실행 폴더의 느슨한 파일이 있으면 키 단위로 덮어쓴다.
    /// 느슨한 파일이 못 읽히면 경고 1줄 남기고 내장 표만 쓴다. 둘 다 없으면 null.</summary>
    private static Dictionary<string, string>? LoadTable(string culture)
    {
        Dictionary<string, string>? table = null;

        try
        {
            using var stream = typeof(CvLoc).Assembly.GetManifestResourceStream($"CvInspect.Assets.lang.cv.{culture}.json");
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                table = ParseFlat(ms.ToArray());
            }
        }
        catch (Exception ex)
        {
            CvLog.Publish(CvLogLevel.Warning, nameof(CvLoc), $"failed to load the embedded table for culture '{culture}'.", ex);
        }

        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "lang", $"cv.{culture}.json");
        try
        {
            if (File.Exists(file))
            {
                var overrides = ParseFlat(File.ReadAllBytes(file));
                if (table is null)
                {
                    table = overrides;
                }
                else
                {
                    foreach (var kv in overrides) table[kv.Key] = kv.Value;
                }
            }
        }
        catch (Exception ex)
        {
            CvLog.Publish(CvLogLevel.Warning, nameof(CvLoc), $"failed to load '{file}' — using the embedded table only.", ex);
        }

        return table;
    }

    /// <summary>평면 객체(문자열→문자열)만 읽는다 — 문자열이 아닌 값은 무시. 주석/후행 콤마는 현장 편집을 고려해 허용.</summary>
    private static Dictionary<string, string> ParseFlat(byte[] json)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString()!;
        }
        return dict;
    }

    private static void WarnOnce(string token, string message)
    {
        lock (_sync)
        {
            if (!_missingLogged.Add(token)) return;
        }
        var handler = MissingKey;
        if (handler is not null) handler(message);
        else CvLog.Publish(CvLogLevel.Warning, nameof(CvLoc), message);
    }
}
