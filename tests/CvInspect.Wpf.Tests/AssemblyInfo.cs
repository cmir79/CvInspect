// WPF Dispatcher 와 CvLog.Sink 는 프로세스 전역이다. 병렬로 돌리면 한 테스트가 바꿔 둔 로그 싱크를 다른 테스트가
// 보게 되어 경고 건수 단언이 흔들린다. 전량이 몇 초라 병렬로 얻을 것이 없다.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
