// 속성 편집기의 UI 무관 타입은 0.34 부터 코어(CvInspect)에 있다. 0.33 까지는 이 어셈블리에 있었다 —
// 그 판에 대고 컴파일한 소비자 어셈블리는 이 타입들을 "CvInspect.Wpf 안의 CvInspect.Controls.XXX" 로 찾으므로,
// 런타임이 코어로 따라가게 전달 표시를 남긴다. 이름·namespace 가 같아 소스는 고칠 것이 없다(코어는 이 패키지가 끌어온다).
// 여기서 하나라도 빠뜨리면 옛 판에 대고 컴파일한 소비자가 실행 중에 TypeLoadException 으로 죽는다 — 목록을 줄이지 않는다.

using System.Runtime.CompilerServices;
using CvInspect.Controls;

[assembly: TypeForwardedTo(typeof(CvPropGroupVm))]
[assembly: TypeForwardedTo(typeof(CvPropRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropBoolRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropNumRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropStrRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropEnumRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropActionRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropListRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropNestedRowVm))]
[assembly: TypeForwardedTo(typeof(CvPropRowBldr))]
[assembly: TypeForwardedTo(typeof(CvPropCommittedEvt))]
[assembly: TypeForwardedTo(typeof(CvPropActionExecutingEvt))]
[assembly: TypeForwardedTo(typeof(CvPropActionExecutedEvt))]
[assembly: TypeForwardedTo(typeof(CvPropActionFailedEvt))]
