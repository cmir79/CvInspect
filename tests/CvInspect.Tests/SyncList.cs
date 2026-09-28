namespace CvInspect.Tests;

/// <summary>다른 스레드(재연결 루프·이벤트 발행 스레드)가 채우고 시험 스레드가 읽는 목록 — 모든 접근을 한 락으로 감싼다.
/// 락 없는 <see cref="List{T}"/> 는 개수를 먼저 올리고 원소를 나중에 넣는다(.NET 8.0.31 List`1.Add 디컴파일 확인).
/// 그래서 <c>made.Count == 2 &amp;&amp; made[1].X</c> 가 그 틈에 null 을 읽어 시험 전체를 거짓으로 떨어뜨릴 수 있었다
/// (형제 저장소가 같은 모양의 하네스에서 부하 중 실측). 저장 순서와 상관없이 락 없는 읽기는 경합이다.
/// ⚠ 일부러 <see cref="IEnumerable{T}"/> 를 구현하지 않는다 — 살아 있는 목록을 LINQ·string.Join 으로 훑으면 쓰는 쪽과 부딪쳐
/// "Collection was modified" 가 단언 문구를 삼킨다. 훑을 때는 <see cref="Snapshot"/> 으로, 문구에는 <see cref="ToString"/> 으로.</summary>
sealed class SyncList<T>
{
    private readonly List<T> _items = new();

    public void Add(T item) { lock (_items) _items.Add(item); }

    /// <summary>지금 개수를 보고 만들어 넣기까지를 한 락 안에서 — "몇 번째 인스턴스면 느리게" 같은 팩토리용.</summary>
    public T AddNew(Func<int, T> make) { lock (_items) { var t = make(_items.Count); _items.Add(t); return t; } }

    public int Count { get { lock (_items) return _items.Count; } }

    public T this[int index] { get { lock (_items) return _items[index]; } }

    /// <summary>없으면 기본값 — 대기 조건 안에서 아직 안 생긴 원소를 읽을 때.</summary>
    public T? At(int index) { lock (_items) return index >= 0 && index < _items.Count ? _items[index] : default; }

    public T? LastOrDefault() { lock (_items) return _items.Count > 0 ? _items[^1] : default; }

    public T[] Snapshot() { lock (_items) return _items.ToArray(); }

    public void Clear() { lock (_items) _items.Clear(); }

    public override string ToString() => string.Join(",", Snapshot());
}
