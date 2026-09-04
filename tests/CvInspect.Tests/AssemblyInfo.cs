// 이 묶음은 프로세스 전역 상태(CvLoc.Culture·CvLoc.Resolver·CvLog.Sink·CamFactory 등록표)를
// 건드린다. 병렬로 돌리면 서로의 설정을 덮어써 간헐 실패가 나는데, 그 실패는 재현이 어렵고
// 원인이 테스트 대상처럼 보인다. 전량이 몇 초라 병렬로 얻을 것이 없다.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
