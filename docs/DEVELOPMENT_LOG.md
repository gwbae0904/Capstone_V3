# 개발 기록 (고잉메리호)

메인 [README](../README.md)에는 심사/시연용 핵심 정보만 담았고, 여기엔 개발 과정에서
겪었던 시행착오와 설계 결정 배경을 자세히 기록합니다. 팀원이나 이후 유지보수자가 같은
문제를 반복해서 겪지 않도록 하기 위한 문서입니다.

---

## 트래킹 방식 변천사

MediaPipe(Python) → 색상 점 쌍+IMU → ArUco → AprilTag 36h11 순서로 시도했으며, 마지막이
현재 채택한 방식입니다.

- **MediaPipe**: 단안 카메라 기반 깊이 추정이 부정확하여 3D 위치 신뢰도가 낮았음
- **색상 마커 점 쌍 + IMU**: 조명 변화에 취약하고 인식 안정성이 낮았음
- **ArUco → AprilTag 36h11**: 마커 하나로 6DoF 포즈 획득 가능, 오탐(false positive)이
  ArUco보다 훨씬 적어 최종 채택

## 주요 스크립트 (`Assets/Scripts/`)

| 파일 | 역할 |
|---|---|
| `ArucoHandTracker.cs` | 웹캠/영상파일 입력 → ArUco/AprilTag 인식 → 위치 계산 → 칼만 필터 → RightHand에 적용. `Preferred Camera Name`으로 특정 웹캠을 이름으로 지정 가능(우선순위: 지정한 이름 → iVCam → 첫 번째 카메라), Play 시 Console에 연결된 카메라 목록 전체를 출력. `Invert Depth`로 거리-깊이 관계 반전 가능. 한 앵글에 마커가 여러 개(손등 3개) 동시에 잡힐 때 `Marker Switch Threshold Ratio`로 잦은 전환을 억제하고, 마커별 `Position Offset`을 현재 회전만큼 돌려서 더해 전환 시 위치 튐을 줄임 |
| `SerialGloveReceiver.cs` | 아두이노 시리얼 수신(curl+IMU), 회전 적용, tare 명령 전송. `Axis Mapping`/`Invert X,Y,Z`로 축 보정. hover/grab 상태에 따라 물체별 햅틱 정지 각도(0~180도, 변환 없이 그대로)를 아두이노로 전송. `Show Debug Info`로 화면 디버그 텍스트 on/off |
| `FingerCurlAnimator.cs` | curl 값으로 장갑 모델의 손가락 뼈를 실제로 굽힘. 관절별(meta/j0/j1/j2) 비율과 `Curl Axis`로 굽힘 방향/깊이 조정. 물체를 잡는 동안은 자동으로 손을 떼고 grasp pose 애니메이터에게 자세를 맡김. `Show Debug Info`로 화면 디버그 텍스트 on/off |
| `GraspPoseTrigger.cs` | 물체를 잡으면 그 물체에 맞는 손모양(`Rest`/`SphereGrab`/`StickGrab`/`pinchGrab`)으로 `Animator.Play()`로 즉시 전환. 손가락별 햅틱 정지 각도(0~180도, 아두이노에 변환 없이 직접 전송), 물체별 커스텀 Grab Threshold도 여기서 설정. **`Assets/Scripts/`와 `Assets/SteamVR/InteractionSystem/Core/Scripts/` 두 곳에 동일한 내용으로 있어야 함** (아래 어셈블리 분리 이슈 참고) |
| `HandCollider.cs` | 원본 SteamVR HandPhysics 계열 코드. **손가락 관절별 물리 충돌을 시도했으나 이름 충돌 버그로 되돌림** (아래 "물리 손 콜라이더 시도와 되돌림" 참고) — 현재는 씬에서 제거된 상태 |
| `ResetObjectsOnKeyPress.cs` | `Backspace` 키로, 씬의 `Throwable` 붙은 물체(Sphere/Cube 등)를 전부 자동으로 찾아 Play 시작 시점의 위치/회전으로 되돌리고 속도도 0으로 리셋. 테스트 중 물체를 이리저리 던지고 잡은 뒤 빠르게 초기화할 때 사용 |
| `PrimitiveSizeSetter.cs` | Unity 기본 프리미티브(Cube/Sphere) 전용. `Size In Meters`에 정육면체면 한 변 길이, 구면 지름을 입력하면 그 실제 크기로 자동 스케일 조정. `[ExecuteAlways]`라 에디터에서 값 바꾸면 Play 없이 바로 반영됨 |
| `QuitOnKeyPress.cs` | 지정한 키(기본 Esc)를 누르면 프로그램 종료. 빌드된 실행 파일에는 에디터의 정지 버튼이 없어서 추가함 |
| `KeyboardHandDriver.cs` | 하드웨어 없이 WASD+Space로 테스트할 때 사용 |
| `FallbackCameraController.cs` | 헤드셋 없이 WASD+마우스 우클릭으로 시점 조작 (개발용). `Show Instructions`로 화면 안내 문구 on/off |

### `Assets/SteamVR/InteractionSystem/Core/Scripts/`에서 수정한 것들

| 파일 | 수정 내용 |
|---|---|
| `Hand.cs` | grab 판정을 FixedUpdate→Update로 이동(저프레임 대응). `SnapOnAttach` 실제 처리 로직 추가, `objectAttachmentPoint`에 직접 부모로 붙여서 잡은 뒤에도 손 모양 보정을 계속 따라가게 함. `GetTrackedObjectVelocity/AngularVelocity` 스무딩. hover 대상을 가장 가까운 것 하나로 제한(물체 여러 개가 겹쳐있을 때 하나가 안 놓아지던 문제 해결). 물체별 커스텀 Grab Threshold 지원. **`Hand Collider` 필드 추가 — `LateUpdate()`에서 매 프레임 손의 최신 위치/회전을 `HandCollider.MoveTo()`로 전달, 물체를 잡거나 놓을 때 `SetCollisionDetectionEnabled()`로 충돌 감지를 켜고 끔** (잡은 물체와 손 콜라이더가 서로 밀어내며 떨리는 것 방지) |
| `HandVisual.cs` | 장갑 3D 모델을 인스턴스화하고, 지정한 뼈(`Target Bone Name`)가 항상 RightHand 원점에 오도록 매 프레임 재정렬. `HoverPoint`/`ObjectAttachmentPoint`도 모델과의 상대 위치+회전 관계를 캡처해서 매 프레임 재현. `Visual Rotation Offset Euler`로 모델의 기본 조형 각도도 보정 가능 |

## 물리 손 콜라이더 시도와 되돌림 (`HandCollider` / `HandColliderRight.prefab`)

손이 물체를 그대로 통과하던 문제를 해결하기 위해, 원본 SteamVR의 물리 기반 손(Physics
Hand) 시스템을 가져와 통합을 시도했으나, **아래 이름 충돌 버그 때문에 결국 되돌리고
`Hand.cs`를 이전 버전으로, 씬에서 `HandColliderRight`를 삭제**했습니다. 나중에 다시
시도한다면 이 문제를 반드시 피해야 합니다.

- **구조**: `HandColliderRight` 프리팹 안에 손가락 관절 이름(`finger_index_1_r` 등, Valve
  표준 스켈레톤 명명 규칙)을 딴 Transform들이 있고, 각각에 `SphereCollider`가 붙어있었음.
  엄지는 1개, 검지/중지는 3개(전체 관절), 약지/소지는 2개(밑동 제외)만 연결
- **`Hand` 필드가 `[HideInInspector]`로 숨겨져 있던 문제**: 원래 코드에 이 속성이 붙어있어
  Inspector에서 아예 안 보였음 → 속성을 제거해서 수동 연결 가능하게 함
- **`Is Kinematic`은 꺼져 있는 게 맞았음**: "코드로 위치를 지정하는 물체는 Kinematic이어야
  한다"는 일반론으로 판단해 켜봤으나, `rigidbody.angularVelocity` 직접 대입 코드에서
  `Setting angular velocity of a kinematic body is not supported` 에러 발생. 이 스크립트는
  애초에 "속도를 계산해서 서서히 밀고 들어가는" 비운동학적 리지드바디를 전제로 설계됨
- **최종적으로 되돌린 결정적 버그 — 이름 충돌**: `HandColliderRight` 안의 관절 Transform
  이름(`finger_index_1_r`, `finger_middle_0_r` 등)이 **진짜 장갑 모델의 뼈 이름과 정확히
  똑같았음**. `FingerCurlAnimator.FindDeepChild()`가 이름만으로 뼈를 검색하는 방식이라,
  씬에 이름이 겹치는 오브젝트가 있으면 어느 쪽을 찾을지 보장이 안 됨. 실제로 검지·중지만
  손가락이 이상하게 안 굽혀지는 증상으로 나타났는데, 하필 검지·중지에만 관절 3개짜리
  콜라이더가 온전히 있었고(약지·소지는 2개뿐) 그게 힌트가 되어 원인을 찾음.
  **`HandColliderRight`를 비활성화하는 것만으론 해결 안 됨** — Unity의 이름 검색은
  비활성화된 오브젝트도 찾아내므로, 완전히 씬에서 삭제해야 충돌이 사라짐. 다시 시도한다면
  관절 콜라이더 이름을 장갑 모델과 겹치지 않게(예: 접미사 추가) 짓는 게 필수

## 개인별로 맞춰야 하는 값 (Inspector에서, 커밋 전에 되돌리기)

- `SerialGloveReceiver` → `Port Name`: 본인 PC의 COM 포트 번호로
- `ArucoHandTracker` → `Preferred Camera Name`: 사용할 웹캠 이름 (여러 대 연결 시)
- `ArucoHandTracker` → `Fx`/`Fy`/`Distance Scale Correction`: 웹캠마다 다름, 실측 거리로 보정
- `ArucoHandTracker` → `Invert Depth`/`Depth Reference Meters`: 거리-깊이 반전 사용 여부와 기준 거리
- `ArucoHandTracker` → `Marker Switch Threshold Ratio`: 마커 여러 개 동시 인식 시 전환 민감도 (기본 1.3)
- `ArucoHandTracker` → `Invert Offset Rotation Direction`/`Invert Marker Rot X,Y,Z`: `Position Offset` 회전 방향이 반대로 나오면 조정
- `ArucoMarkerConfig`(마커별) → `Position Offset`: 같은 면에 마커가 여러 개 있을 때(손등 3개), 기준 마커 대비 실측 필요
- `SerialGloveReceiver` → `Axis Mapping`/`Invert X,Y,Z`: MPU6050 부착 방향에 따라 다름 (현재 값: `YZX`, `Invert X`, `Invert Z`)
- `FingerCurlAnimator` → `Curl Axis`: 장갑 모델 방향에 따라 조정 필요 (현재 값: `(0, 0, -1)`)
- `HandVisual` → `Target Bone Name`/`Additional Offset`/`Visual Rotation Offset Euler`: 마커 부착 위치, 모델 기본 각도에 맞춰서 (현재 회전 오프셋: `X:0, Y:-45, Z:90`)
- `HandVisual` → `Hover Point To Align`/`Attachment Point To Align`: `HoverPoint`/`ObjectAttachmentPoint` 연결 필요
- `Hand` → `Hover Radius`: 손이 물체와 얼마나 가까워야 반응할지
- `ResetObjectsOnKeyPress` → `Reset Key`: 물체 리셋 키 변경 가능 (기본 Backspace)
- `GraspPoseTrigger` → 손가락별 `Stop Angle`(0~180도): 아두이노 프로토콜/서보 반전 설정이 바뀔 때마다 실측 재조정 필요
- `QuitOnKeyPress` → `Quit Key`: 원하는 종료 키로 변경 가능 (기본 Esc)
- `PrimitiveSizeSetter`(Cube/Sphere) → `Size In Meters`: 원하는 물체 크기로 조정

## 알려진 이슈 / 설계 결정 기록

- **회전 담당**: IMU(MPU6050)가 전담. `ArucoHandTracker`의 `Apply Rotation`은 반드시 꺼둘 것 (안 그러면 서로 충돌)
- **마커 기반 IMU 드리프트 자동 보정은 시도했다가 되돌림**: 마커 회전을 절대 기준 삼아 IMU 요(yaw)
  드리프트를 실시간 보정하는 기능을 구현했었으나, 마커/IMU 각각의 축 정의가 서로 다른 경로로
  틀어져 있어 축 보정 조합을 찾기가 지나치게 어려웠음. 실익 대비 튜닝 난이도가 너무 높아 제거하고
  순수 IMU 방식으로 복귀. 손등에 마커 3개를 붙이며 생긴 "동시 인식" 문제만 별도로 해결함
- **손등 마커 3개가 한 앵글에 동시에 잡히는 문제**: 매 프레임 "제일 크게 보이는 마커"만 단순
  비교하면, 크기가 엇비슷할 때 프레임마다 다른 마커로 전환되면서 위치가 튀는 문제가 있었음.
  ① 지금 추적 중인 마커보다 확실히(기본 30% 이상) 커야만 전환하는 히스테리시스, ② 마커별
  고정 `Position Offset`을 현재 측정 회전만큼 돌려서 위치에 더하는 보정, 두 가지로 완화함
- **햅틱 프로토콜 변경**: 처음엔 Unity에서 0~180도 입력을 아두이노가 기대하던 0~1000 값으로
  변환해서 보냈는데, 아두이노 펌웨어가 `H,각도1,...,각도5` 형식으로 0~180도를 직접 받는
  방식으로 바뀌면서 그 변환 로직을 제거함 (`GraspPoseTrigger.cs`, `SerialGloveReceiver.cs`).
  이 과정에서 서보 반전 설정(`servoReverse`)도 전 손가락 켜지는 쪽으로 바뀌어서, 예전에
  실측해둔 각도값이 지금도 같은 물리적 결과를 낼지 보장할 수 없음 — 프로토콜이나 서보 반전
  설정이 바뀔 때마다 손으로 다시 확인 필요
- **웹캠 60fps 확보**: `ReadPixels`(동기) 대신 `AsyncGPUReadback`(비동기) 사용 — 직접 `ReadPixels`로 되돌리면 fps가 다시 떨어짐
- **`GraspPoseTrigger`를 물체에 붙일 때 `[RequireComponent(typeof(Interactable))]`를 쓰면 안 됨** —
  이 씬은 `Interactable`이 보통 부모 오브젝트(`Throwable (...)`)에 있고 자식(Cube 등)에는 없는 구조가 흔함.
  `RequireComponent`를 쓰면 Unity가 자식에 새 `Interactable`을 자동 생성해버려서, 하이라이트/잡기 전체가
  엉뚱한 인스턴스로 새는 문제가 있었음. `GetComponentInParent`로 찾도록 되어있음. 컴포넌트를 뗄 때도
  Remove Component 대신 체크박스만 끄는 걸 권장 (연쇄 삭제 방지)
- **물체를 놓아도 계속 손을 따라다니는 문제**: `Throwable` 컴포넌트의 `Restore Original Parent` 체크박스가
  꺼져있으면 발생. 반드시 켜둘 것
- **`HandVisual`의 뼈 정렬을 `Awake()`에서 한 번만 계산하면 안 됨** — 그 시점의 회전 상태에 따라
  나중에 회전축이 엉뚱한 곳(엄지 등)으로 보일 수 있음. 반드시 `LateUpdate`에서 매 프레임 재계산할 것
- **`HandVisual`에서 `HoverPoint`/`ObjectAttachmentPoint`와 모델의 관계를 캡처할 때, 캡처 시점에
  `Visual Rotation Offset Euler`에 이미 0이 아닌 값이 들어있으면 그 값 기준으로 캡처되어버려서,
  Play를 그 값으로 시작하면 물체가 손 회전과 무관하게 "회전 0일 때 위치"에서 잡히는 버그가 있었음** —
  캡처는 항상 회전 오프셋을 identity로 강제한 뒤에 하도록 수정
- **`Hand.AttachObject`가 물체를 `RightHand` 자신에게 붙이고 잡는 순간에만 위치를 복사하던 방식이었는데,
  그러면 잡은 뒤에는 `HandVisual`이 `objectAttachmentPoint`에 주는 회전 보정이 전혀 반영이 안 됐음** —
  `objectAttachmentPoint`에 직접 부모로 붙이도록 수정
- **물체 2개가 서로 가깝게/겹쳐 배치되어 있으면, 손 1개로 잡을 때 하나가 제대로 안 놓아지는 문제**:
  둘 다 동시에 hover 상태가 되면서 grab 시작 프레임에 둘 다 `AttachObject`를 시도하는 경합이
  원인이었음. `Hand.UpdateHovering()`이 가장 가까운 물체 하나만 hover 대상으로 삼도록 수정해서 해결
- **`Assets/SteamVR/...`는 `Assets/Scripts/`와 별도의 컴파일 단위(어셈블리)로 나뉘어 있음** —
  `Assets/Scripts/`에 있는 클래스를 `Assets/SteamVR/...` 쪽 스크립트에서 참조하면
  `CS0246` 에러가 남. 지금은 `Hand.cs`가 `GraspPoseTrigger`를 참조하고 있어서,
  `GraspPoseTrigger.cs`를 양쪽 폴더에 동일한 내용으로 넣어야 함
- **Unity 6 + OpenCvSharp에서 API 이름이 계속 다름**: `PredefinedDictionaryType`, `DetectorParameters`,
  `SolvePnPMethod.IPPE_SQUARE` 등 확실하지 않으면 IDE 자동완성으로 확인하는 게 제일 빠름
- **`SerialPort.ReadExisting()`이 Unity(Mono)에서 가끔 에러를 던지는 알려진 버그** — `BytesToRead` +
  `Read(buffer, offset, count)`로 직접 고정 바이트 버퍼를 읽는 방식으로 대체함
- **Console에 "The referenced script (Unknown) on this Behaviour is missing!" 경고가 뜨는 경우**:
  예전에 붙였다가 지운 스크립트의 빈 컴포넌트 잔재. 기능엔 영향 없음
- **`Assets/_Recovery/` 폴더**: Unity 6.x의 크래시 복구 기능이 자동 생성하는 임시 씬 데이터.
  `.gitignore`에 포함되어 있어 커밋 안 됨
- **빌드본 화질이 에디터 Game 창보다 좋아 보이는 것은 정상**: 에디터의 Game 창은 좁은 영역에
  맞춰 축소 렌더링되는 미리보기이고, 빌드본이 실제 해상도로 렌더링하는 최종 결과물임
- **빌드본엔 에디터의 정지(Pause)/정지(Stop) 버튼이 없음**: 개발용 도구라 빌드에는 포함
  안 됨. `QuitOnKeyPress.cs`로 별도 종료 키(Esc)를 만들어서 대응함
- **모터가 노이즈처럼 미세하게 계속 떨리는 문제**: hover/grab 상태인 동안 `SerialGloveReceiver`가
  매 프레임(초당 수십 번) 완전히 똑같은 햅틱 명령을 아두이노로 계속 반복 전송하고 있었음.
  값이 그대로인데도 시리얼 라인이 쉴 새 없이 몰리면서, 아주 가끔 한 줄이 중간에 끊기거나
  겹쳐 아두이노가 순간적으로 잘못된 값을 읽는 것으로 추정됨 (직접 아두이노 앱으로 한 번만
  명령을 보내면 안 떨리는 것으로 비교 확인). 마지막으로 보낸 값과 실제로 달라졌을 때만
  전송하도록 수정해서 해결
- **`Assets/Scripts/`와 `Assets/SteamVR/.../Scripts/`에 있는 두 `GraspPoseTrigger.cs`가
  서로 다른 상태로 벌어지는 문제**: 물체별 `Custom Grab Threshold`가 극단적인 값(0.05)을
  넣어도 전혀 반영이 안 되는 증상이 있었음. 원인은 두 폴더가 서로 다른 컴파일 단위라, 오브젝트에
  실제로 붙어있는 컴포넌트가 어느 한쪽 코드만 최신 상태였고 다른 한쪽(예: `Hand.cs`가 참조하는
  쪽)은 예전 버전이라 새 필드 자체가 없었던 것. 파일 내용을 똑같이 맞추는 것만으론 부족하고,
  **두 스크립트를 실제로 오브젝트에 모두 컴포넌트로 붙여야** 함(하나만 붙어있으면 다른 한쪽
  어셈블리 코드에서는 항상 null로 보임) — Inspector 컴포넌트 헤더 우클릭 → `Edit Script`로
  지금 어느 파일이 실제로 붙어있는지 확인 가능
- **웹캠 요청 해상도 960이 자동으로 720으로 낮아지던 문제**: `ArucoHandTracker`가
  1280x960@60fps를 요청했는데, 960이라는 세로 해상도가 이 카메라의 표준 지원 해상도가
  아니라서 드라이버가 제일 가까운 720으로 대신 내려버렸던 것. 카메라가 실제로 1920x1080@60fps를
  지원하는 것을 Windows 카메라 설정에서 확인했으나, 막상 1920x1080으로 올려보니 이 프로젝트
  기준으로는 부하가 커서 프레임이 떨어져 다시 1280x960으로 되돌림 — 해상도를 바꿀 때는 성능
  저하 여부를 실제로 확인 후 결정할 것
