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
| `ArucoHandTracker.cs` | 웹캠/영상파일 입력 → ArUco/AprilTag 인식 → 위치 계산 → 칼만 필터 → RightHand에 적용. `Use IVCam`으로 웹캠 소스 자동 탐색. `Invert Depth`로 거리-깊이 관계 반전 가능. 마커별 회전/위치 보정값으로 IMU 드리프트 보정용 절대 방향과 손목 기준 위치를 계산해 공개 |
| `SerialGloveReceiver.cs` | 아두이노 시리얼 수신(curl+IMU), 회전 적용, tare 명령 전송. `Axis Mapping`/`Invert X,Y,Z`로 축 보정. hover/grab 상태에 따라 물체별 햅틱 정지 각도를 아두이노로 전송. `ArucoHandTracker`가 공개하는 마커 기반 절대 방향으로 IMU 드리프트를 매 프레임 부드럽게 보정 |
| `FingerCurlAnimator.cs` | curl 값으로 장갑 모델의 손가락 뼈를 실제로 굽힘. 관절별(meta/j0/j1/j2) 비율과 `Curl Axis`로 굽힘 방향/깊이 조정. 물체를 잡는 동안은 자동으로 손을 떼고 grasp pose 애니메이터에게 자세를 맡김 |
| `GraspPoseTrigger.cs` | 물체를 잡으면 그 물체에 맞는 손모양(`Rest`/`SphereGrab`/`StickGrab`/`pinchGrab`)으로 `Animator.Play()`로 즉시 전환. 손가락별 햅틱 정지 각도(0~180도), 물체별 커스텀 Grab Threshold도 여기서 설정. **`Assets/Scripts/`와 `Assets/SteamVR/InteractionSystem/Core/Scripts/` 두 곳에 동일한 내용으로 있어야 함** (아래 어셈블리 분리 이슈 참고) |
| `KeyboardHandDriver.cs` | 하드웨어 없이 WASD+Space로 테스트할 때 사용 |
| `FallbackCameraController.cs` | 헤드셋 없이 WASD+마우스 우클릭으로 시점 조작 (개발용) |

### `Assets/SteamVR/InteractionSystem/Core/Scripts/`에서 수정한 것들

| 파일 | 수정 내용 |
|---|---|
| `Hand.cs` | grab 판정을 FixedUpdate→Update로 이동(저프레임 대응). `SnapOnAttach` 실제 처리 로직 추가, `objectAttachmentPoint`에 직접 부모로 붙여서 잡은 뒤에도 손 모양 보정을 계속 따라가게 함. `GetTrackedObjectVelocity/AngularVelocity` 스무딩. hover 대상을 가장 가까운 것 하나로 제한(물체 여러 개가 겹쳐있을 때 하나가 안 놓아지던 문제 해결). 물체별 커스텀 Grab Threshold 지원 (`GraspPoseTrigger`의 `Use Custom Grab Threshold`) |
| `HandVisual.cs` | 장갑 3D 모델을 인스턴스화하고, 지정한 뼈(`Target Bone Name`)가 항상 RightHand 원점에 오도록 매 프레임 재정렬. `HoverPoint`/`ObjectAttachmentPoint`도 모델과의 상대 위치+회전 관계를 캡처해서 매 프레임 재현. `Visual Rotation Offset Euler`로 모델의 기본 조형 각도도 보정 가능 |

## 개인별로 맞춰야 하는 값 (Inspector에서, 커밋 전에 되돌리기)

- `SerialGloveReceiver` → `Port Name`: 본인 PC의 COM 포트 번호로
- `ArucoHandTracker` → `Fx`/`Fy`/`Distance Scale Correction`: 웹캠마다 다름, 실측 거리로 보정
- `ArucoHandTracker` → `Invert Depth`/`Depth Reference Meters`: 거리-깊이 반전 사용 여부와 기준 거리
- `SerialGloveReceiver` → `Axis Mapping`/`Invert X,Y,Z`: MPU6050 부착 방향에 따라 다름 (현재 값: `YZX`, `Invert X`, `Invert Z`)
- `SerialGloveReceiver` → `Vision Correction Speed Deg Per Sec`: 마커 기반 IMU 보정이 얼마나 빠르게 따라잡을지
- `FingerCurlAnimator` → `Curl Axis`: 장갑 모델 방향에 따라 조정 필요 (현재 값: `(0, 0, -1)`)
- `HandVisual` → `Target Bone Name`/`Additional Offset`/`Visual Rotation Offset Euler`: 마커 부착 위치, 모델 기본 각도에 맞춰서 (현재 회전 오프셋: `X:0, Y:-45, Z:90`)
- `HandVisual` → `Hover Point To Align`/`Attachment Point To Align`: `HoverPoint`/`ObjectAttachmentPoint` 연결 필요
- `Hand` → `Hover Radius`: 손이 물체와 얼마나 가까워야 반응할지
- `ArucoMarkerConfig`(마커별) → `Hand Rotation Offset Euler`/`Marker To Wrist Offset`: 마커 부착 위치 확정 후 실측

## 알려진 이슈 / 설계 결정 기록

- **회전 담당**: IMU(MPU6050)가 전담하되 마커 기반으로 보정. `ArucoHandTracker`의 `Apply Rotation`은 반드시 꺼둘 것 (안 그러면 서로 충돌)
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
- **마커 기반 IMU 보정을 추가한 배경**: MPU6050은 지자기 센서가 없어 요(yaw) 방향이 시간이
  지나며 서서히 어긋남(드리프트). 매번 수동으로 재보정(T키)하는 대신, 마커가 보일 때마다
  ArucoHandTracker가 계산하는 "카메라 기준 절대 방향"으로 IMU 값을 초당 일정 속도만큼
  부드럽게 끌어당기도록 구현. 마커가 안 보이면 마지막 보정값을 유지한 채 IMU만으로 계속 추적
- **Unity 6 + OpenCvSharp에서 API 이름이 계속 다름**: `PredefinedDictionaryType`, `DetectorParameters`,
  `SolvePnPMethod.IPPE_SQUARE` 등 확실하지 않으면 IDE 자동완성으로 확인하는 게 제일 빠름
- **`SerialPort.ReadExisting()`이 Unity(Mono)에서 가끔 에러를 던지는 알려진 버그** — `BytesToRead` +
  `Read(buffer, offset, count)`로 직접 고정 바이트 버퍼를 읽는 방식으로 대체함
- **Console에 "The referenced script (Unknown) on this Behaviour is missing!" 경고가 뜨는 경우**:
  예전에 붙였다가 지운 스크립트의 빈 컴포넌트 잔재. 기능엔 영향 없음
- **`Assets/_Recovery/` 폴더**: Unity 6.x의 크래시 복구 기능이 자동 생성하는 임시 씬 데이터.
  `.gitignore`에 포함되어 있어 커밋 안 됨
