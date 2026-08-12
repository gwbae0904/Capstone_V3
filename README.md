# 고잉메리호 — VR 햅틱 글러브 (Capstone V3)

건국대학교 캡스톤디자인 프로젝트. 웹캠 기반 마커 트래킹 + 아두이노(포텐셔미터+IMU)로
손 위치·회전·손가락 굽힘을 실시간으로 읽어들여, Unity 안의 가상 손을 움직이고
물체를 잡으면 서보모터로 촉각 피드백을 주는 시스템입니다.

> ⚠️ 이 저장소는 이전 버전(Python + MediaPipe 방식)과 아키텍처가 완전히 다릅니다.
> Python은 더 이상 사용하지 않고, 모든 컴퓨터 비전 처리를 Unity 안에서 OpenCvSharp로 직접 수행합니다.

---

## 시스템 구조

```
[웹캠]
   ↓ (ArUco/AprilTag 마커 인식, OpenCvSharp)
[손 위치]
   ↓
[Unity 가상 손]  ←──  [아두이노: 포텐셔미터(손가락 굽힘) + MPU6050(손 회전, IMU)]
   ↓                        ↑ USB 시리얼 (115200 baud)
[물체 grab/throw]      [서보모터 (손가락별 저항, Lucas glove 방식 - 아래 참고)]
```

- **위치**: 손 여러 면(손등/손바닥/손날 등)에 AprilTag(36h11) 마커를 붙이고, 웹캠으로 인식 → 위치만 담당
- **회전**: MPU6050(IMU)이 전담 (ArUco는 위치만, 서로 역할 분리됨 — 안 그러면 충돌함)
- **손가락 굽힘**: 아두이노에 연결된 포텐셔미터 5개(엄지~소지) 값을 시리얼로 수신, 실제 손가락 뼈에 반영
- **잡기**: 물체를 잡으면 그 물체에 맞는 손모양(SphereGrab 등)으로 자동 전환, 손 위치·회전 보정까지 정확히 반영되어 스냅됨. 반경 안에 물체가 여러 개 있어도 가장 가까운 것 하나만 잡히도록 처리됨
- **햅틱**: 손가락별 서보모터가 물체 크기에 맞는 각도로 미리 세팅되어, 실제 저항은 기계 구조(아래 참고)가 만들어냄

---

## 개발 환경

| 항목 | 버전 |
|---|---|
| Unity | 6.3 LTS (6000.3.19f1) |
| 렌더 파이프라인 | Built-in |
| API Compatibility Level | **.NET Framework** (시리얼 통신에 필수, 아래 참고) |
| 컴퓨터 비전 | OpenCvSharp4 (NuGet) |
| 마커 시스템 | ArUco / AprilTag (36h11 사용 중) |
| 베이스 | SteamVR Interaction System (OpenVR 의존성 대부분 제거 후 커스터마이징) |
| 아두이노 | ESP32 + MPU6050 (DMP) + 가변저항 5개 + 서보모터 5개 |

---

## 처음 클론 받았을 때 셋업 순서

### 1. 저장소 클론
```bash
git clone https://github.com/gwbae0904/Capstone_V3.git
```

### 2. Unity로 열기
Unity Hub → Open → 클론한 폴더 선택. Unity 6.3 LTS(6000.3.19f1)가 없으면 Unity Hub에서 먼저 설치.
처음 열 때 `Library/` 폴더를 새로 만드느라 시간이 좀 걸립니다 (정상입니다, 기다려주세요).

### 3. NuGetForUnity로 OpenCvSharp 설치
`Assets/Packages/`는 용량 문제로 저장소에 안 올라가 있어서, **클론 후 매번 새로 설치해야 합니다.**

1. [NuGetForUnity 릴리즈 페이지](https://github.com/GlitchEnzo/NuGetForUnity/releases)에서 최신 `.unitypackage` 다운로드
2. Unity가 켜진 상태에서 그 파일을 더블클릭 → Import
3. Unity 상단 메뉴에 **NuGet** 항목이 생기면, `NuGet → Manage NuGet Packages` 클릭
4. 검색창에 `opencvsharp4` 입력 후:
   - **`OpenCvSharp4`** 설치
   - **`OpenCvSharp4.runtime.win`** 설치 (native 바인딩, 필수)

### 4. API Compatibility Level 변경 (아두이노 시리얼 통신에 필수)
`Edit → Project Settings → Player → Other Settings → Configuration → Api Compatibility Level`을
**`.NET Framework`**로 변경. (기본값인 .NET Standard 2.1에는 `System.IO.Ports.SerialPort`가 없어서
컴파일 에러가 납니다.)

### 5. 시리얼 포트 확인
`RightHand`의 `Serial Glove Receiver` 컴포넌트 → `Port Name`을 본인 PC의 실제 COM 포트 번호로 설정
(장치관리자 → 포트(COM & LPT)에서 확인).

### 6. Play 눌러서 웹캠/마커 인식 확인
`DotTracker`(또는 `ArucoHandTracker`가 붙은 오브젝트) 선택 → Inspector에서 웹캠이 잡히는지,
마커를 비췄을 때 인식되는지 확인.

---

## 마커 준비 (팀원 각자 인쇄해서 테스트할 때)

- **딕셔너리**: `AprilTag 36h11` (ArUco 4x4가 아님, 반드시 이걸로)
- **크기**: 웹캠-손 거리 30~40cm 기준 **2cm × 2cm** 권장 (공간 부족하면 1.2~1.5cm까지 가능)
- **생성 사이트**: [chev.me/arucogen](https://chev.me/arucogen) 등에서 Dictionary를 AprilTag 36h11로 선택
- 각 마커 ID는 `ArucoHandTracker`의 `Markers` 리스트에 등록된 ID(0, 1, 2, 3...)와 정확히 일치해야 함
- **마커를 실제로 어디에 붙였는지(손목, 손등 등)에 따라 `HandVisual`의 `Target Bone Name`을 맞춰야 함**

---

## 카메라 관련 참고

- 웹캠 외에 **iVCam**(휴대폰을 웹캠처럼 쓰는 앱) 연결도 지원함 — `ArucoHandTracker`의 `Use IVCam` 체크박스로 자동 탐색 여부 조절 (켜져 있으면 iVCam 우선 탐색, 없으면 내장 웹캠으로 폴백)
- 아이폰 등 휴대폰의 고프레임(120fps 등) 슬로우모션 촬영 기능은 **실시간 웹캠 스트리밍으로는 활용 불가**(현재 나온 웹캠 연결 앱들은 최대 60fps까지만 지원). 다만 슬로우모션으로 찍은 영상 파일을 `Input Source: Video File` 모드로 재생시켜서, 고프레임 상황에서의 인식 성능을 미리 테스트해보는 용도로는 활용 가능

---

## 주요 스크립트 (`Assets/Scripts/`)

| 파일 | 역할 |
|---|---|
| `ArucoHandTracker.cs` | 웹캠/영상파일 입력 → ArUco/AprilTag 인식 → **위치만** 계산 → 칼만 필터 → RightHand에 적용. `Use IVCam`으로 웹캠 소스 자동 탐색. `Invert Depth`로 카메라와의 거리-화면상 깊이 관계를 반전 가능(아래 참고) |
| `SerialGloveReceiver.cs` | 아두이노 시리얼 수신(curl+IMU), **회전** 적용, tare 명령 전송. `Axis Mapping`/`Invert X,Y,Z`로 축 보정. hover/grab 상태에 따라 물체별 햅틱 정지값을 아두이노로 전송 (아래 햅틱 섹션 참고) |
| `FingerCurlAnimator.cs` | curl 값으로 장갑 모델의 손가락 뼈를 실제로 굽힘. 관절별(meta/j0/j1/j2) 비율과 `Curl Axis`로 굽힘 방향/깊이 조정. 물체를 잡는 동안은 자동으로 손을 떼고 grasp pose 애니메이터에게 자세를 맡김 |
| `GraspPoseTrigger.cs` | 물체를 잡으면 그 물체에 맞는 손모양(`Rest`/`SphereGrab`/`StickGrab`/`pinchGrab`)으로 `Animator.Play()`로 즉시 전환. 손가락별 햅틱 정지 각도(아래 참고), 물체별 커스텀 Grab Threshold도 여기서 설정. **`Assets/Scripts/`와 `Assets/SteamVR/InteractionSystem/Core/Scripts/` 두 곳에 동일한 내용으로 있어야 함** (아래 "알려진 이슈" 참고) |
| `KeyboardHandDriver.cs` | 하드웨어 없이 WASD+Space로 테스트할 때 사용 |
| `FallbackCameraController.cs` | 헤드셋 없이 WASD+마우스 우클릭으로 시점 조작 (개발용) |

### `Assets/SteamVR/InteractionSystem/Core/Scripts/`에서 수정한 것들

| 파일 | 수정 내용 |
|---|---|
| `Hand.cs` | grab 판정을 FixedUpdate→Update로 이동(저프레임 대응). `SnapOnAttach` 실제 처리 로직 추가, `objectAttachmentPoint`에 직접 부모로 붙여서 잡은 뒤에도 손 모양 보정을 계속 따라가게 함. `GetTrackedObjectVelocity/AngularVelocity` 스무딩. **hover 대상을 가장 가까운 것 하나로 제한**(물체 여러 개가 겹쳐있을 때 하나가 안 놓아지던 문제 해결). **물체별 커스텀 Grab Threshold 지원** — 지금 hover/grab 중인 물체의 `GraspPoseTrigger`에서 `Use Custom Grab Threshold`가 켜져 있으면 그 값을, 아니면 `Hand`의 기본 `Grab Threshold`를 사용 (이 때문에 `GraspPoseTrigger` 타입을 참조하게 됨) |
| `HandVisual.cs` | 장갑 3D 모델을 인스턴스화하고, 지정한 뼈(`Target Bone Name`)가 항상 RightHand 원점에 오도록 매 프레임 재정렬. `HoverPoint`/`ObjectAttachmentPoint`도 모델과의 상대 위치+회전 관계를 캡처해서 매 프레임 재현. `Visual Rotation Offset Euler`로 모델 기본 각도 보정 |

---

## 햅틱 피드백 원리 (Lucas glove 방식)

이 프로젝트의 서보모터는 **회전축과 수직으로 배치**되어 있어서, 다음과 같은 방식으로 저항을 만듭니다:

1. 모터를 미리 원하는 각도로 돌려둠 (Unity에서 0~180도로 직접 입력 → 내부적으로 아두이노가 기대하는 0~1000 값으로 변환해서 전송, 아두이노 펌웨어 자체는 안 건드림)
2. 사용자가 실제로 손가락을 굽히면, 손가락 회전축에 달린 나사가 모터 축에 닿는 지점에서 **물리적으로 더 이상 굽혀지지 않게 막힘**
3. 즉 보내는 값은 "얼마나 세게 쥐는지"가 아니라 **"이 물체를 잡을 때 손가락이 어디까지 굽혀지도록 허용할지(=물체 크기)"**를 의미함

### 동작 흐름

| 상태 | 모터 |
|---|---|
| 물체 근처 아님 | 0 (완전히 풀림) |
| 물체에 hover만 함 (아직 안 잡음) | 그 물체의 정지값으로 미리 세팅 (실제로 잡기 전에 준비) |
| 실제로 잡음 | 같은 값 유지 (저항은 기계적 스토퍼가 담당) |
| hover 풀리거나 물체를 놓음 | 다시 0으로 원복 |
| Unity 시작 시(포트 연결 성공 순간) | 이전 세션 값이 남아있을 수 있어 한 번 더 0으로 리셋 |

### 손가락별 정지 각도 (`GraspPoseTrigger`)

물체마다 `Thumb/Index/Middle/Ring/Pinky Stop Angle`(0~180도, 실제 서보 각도 기준)을 따로
설정합니다. 각도가 작을수록 더 많이 굽혀짐(0도=완전히 굽힘), 클수록 일찍 멈춤(180도=편 상태).
시리얼로 나가는 값은 여전히 0~1000이며(아두이노 펌웨어는 그대로), `GraspPoseTrigger`가
`angle = 180 - value/1000×180` 공식의 역산으로 내부 변환합니다.

코드 기본값은 `Thumb=162°, Index=72°, Middle=0°, Ring=72°, Pinky=0°`로 맞춰뒀지만(기존
0~1000 튜닝값 100/600/1000/600/1000과 물리적으로 동일), **이건 새로 물체에 컴포넌트를 붙일
때의 시작값일 뿐**이며, 실제 씬에 이미 있는 Sphere/Cube의 값은 씬 파일에 저장된 값을 그대로
씁니다 (코드 기본값 변경이 기존 오브젝트에 소급 적용되지 않음 — 이미 있는 오브젝트 값을
일괄로 바꾸려면 Hierarchy에서 다중 선택 후 Inspector에서 한 번에 수정).

이 값들은 **실제로 손으로 만져보면서 튜닝하는 값**이라, 정답이 없습니다. 새 물체를 추가하면
직접 잡아보면서 손가락별로 조정하세요.

### 물체별 Grab Threshold (`GraspPoseTrigger`)

물체마다 `Use Custom Grab Threshold`를 켜면, 그 물체를 잡을 때는 `Hand`의 기본
`Grab Threshold`(기본 0.7) 대신 그 물체의 `Custom Grab Threshold`(0~1) 값을 사용합니다.
얇거나 잡기 까다로운 물체는 더 많이 굽혀야 "잡았다"고 인식되도록 개별 조정할 때 사용.
꺼두면(기본값) 항상 `Hand`의 기본값을 따릅니다.

---

## 깊이(거리) 반전 옵션

`ArucoHandTracker`의 `Invert Depth`를 켜면, 카메라와 손 사이의 실제 거리와 가상 손이
화면에서 느껴지는 깊이의 관계를 반대로 만들 수 있습니다.

- **꺼짐(기본)**: 손이 카메라에 가까워지면 가상 손도 화면 앞쪽으로, 멀어지면 가상 손도 화면
  안쪽으로 — 실제 거리와 동일한 방향
- **켜짐**: 기준 거리(`Depth Reference Meters`)를 중심으로 반대로 동작 — 손이 카메라에
  가까워지면 가상 손은 오히려 화면 안쪽으로 멀어지고, 손이 카메라에서 멀어지면 가상 손은
  화면 앞쪽으로 가까워짐

계산식: `결과 거리 = 2 × 기준거리 - 실제거리` (기준거리 지점에서는 반전 여부와 무관하게
위치가 그대로 유지되는, 기준점을 중심으로 한 거울 반사 방식)

---

## 개인별로 맞춰야 하는 값 (Inspector에서, 커밋 전에 되돌리기)

- `SerialGloveReceiver` → `Port Name` : 본인 PC의 COM 포트 번호로
- `ArucoHandTracker` → `Fx`/`Fy`/`Distance Scale Correction` : 웹캠마다 다름, 실측 거리로 보정
- `ArucoHandTracker` → `Invert Depth`/`Depth Reference Meters` : 거리-깊이 반전 사용 여부와, 반전 기준이 되는 실측 거리(기본 0.35m)
- `SerialGloveReceiver` → `Axis Mapping`/`Invert X,Y,Z` : MPU6050 부착 방향에 따라 다름 (현재 값: `Axis Mapping = YZX`, `Invert X`, `Invert Z` 켜짐)
- `FingerCurlAnimator` → `Curl Axis` : 장갑 모델 방향에 따라 조정 필요 (현재 값: `(0, 0, -1)`), `Thumb/Other Finger Max Angle`로 굽힘 깊이 조정
- `HandVisual` → `Target Bone Name`/`Additional Offset` : 마커를 실제로 붙인 위치에 맞춰서
- `HandVisual` → `Visual Rotation Offset Euler` : 모델 기본 각도가 이상하면(예: 악수하는 모양) 조정 (현재 값: `X:0, Y:-45, Z:90`)
- `HandVisual` → `Hover Point To Align`/`Attachment Point To Align` : `HoverPoint`/`ObjectAttachmentPoint` 오브젝트를 연결해둬야 함
- `Hand` → `Hover Radius` : 손이 물체와 얼마나 가까워야 반응할지 (너무 멀리서도 반응하면 줄이기)

**주의**: `RightHand`는 `Player.prefab` 안에 있는 오브젝트라, 위 값들이 씬 파일이 아니라
`Player.prefab` 쪽에 프리팹 오버라이드로 저장될 수 있습니다. 커밋 전 `git status`로 어느
파일이 수정됐는지 꼭 확인하세요 (씬 파일만 뜰 수도, 프리팹도 같이 뜰 수도 있음).

---

## 알려진 이슈 / 설계 결정 기록

- **트래킹 방식 변천사**: MediaPipe(Python) → 색상 점 쌍+IMU → ArUco → AprilTag 36h11 (순서대로 시도, 마지막이 현재 채택)
- **회전 담당**: IMU(MPU6050)가 전담. `ArucoHandTracker`의 `Apply Rotation`은 반드시 꺼둘 것 (안 그러면 서로 충돌)
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
- **Unity 6 + OpenCvSharp에서 API 이름이 계속 다름**: `PredefinedDictionaryType`, `DetectorParameters`,
  `SolvePnPMethod.IPPE_SQUARE` 등 확실하지 않으면 IDE 자동완성으로 확인하는 게 제일 빠름
- **`SerialPort.ReadExisting()`이 Unity(Mono)에서 가끔 에러를 던지는 알려진 버그** — `BytesToRead` +
  `Read(buffer, offset, count)`로 직접 고정 바이트 버퍼를 읽는 방식으로 대체함
- **`Assets/SteamVR/...`는 `Assets/Scripts/`와 별도의 컴파일 단위(어셈블리)로 나뉘어 있음** —
  `Assets/Scripts/`에 있는 클래스를 `Assets/SteamVR/...` 쪽 스크립트에서 참조하면
  `CS0246: The type or namespace name 'XXX' could not be found` 에러가 남. 지금은
  `Hand.cs`가 `GraspPoseTrigger`를 참조하고 있어서, **`GraspPoseTrigger.cs`를
  `Assets/Scripts/`와 `Assets/SteamVR/InteractionSystem/Core/Scripts/` 두 곳에 동일한
  내용으로 넣어야 함** (같은 이름의 클래스가 서로 다른 어셈블리에 있는 건 문제없음).
  `Assets/SteamVR/...` 쪽 스크립트에서 `Assets/Scripts/`의 다른 클래스를 새로 참조하게
  되면 이 문제가 또 발생할 수 있음 — 그때마다 해당 파일을 양쪽에 복사해둘 것
- **Console에 "The referenced script (Unknown) on this Behaviour is missing!" 경고가 뜨는 경우**:
  예전에 붙였다가 지운 스크립트의 빈 컴포넌트 잔재. 기능엔 영향 없음, Inspector에서
  `Missing (Mono Script)` 컴포넌트를 찾아 Remove Component 하면 됨
- **`Assets/_Recovery/` 폴더**: Unity 6.x의 크래시 복구 기능이 자동 생성하는 임시 씬 데이터.
  `.gitignore`에 포함되어 있어 커밋 안 됨. 이미 커밋되어 있다면 `git rm -r --cached Assets/_Recovery`로 제거 필요

---

## 아두이노 (하드웨어 담당자용)

- 보드: **ESP32** + **MPU6050**(DMP 내장, 지자기 센서 없어서 요(yaw) 드리프트 있을 수 있음 → `T`키로 주기적 재조절)
- `Arduino/GloveFirmware/GloveFirmware.ino` 참고
- 시리얼 프로토콜 (115200 baud):
  ```
  아두이노 → Unity:  c0,c1,c2,c3,c4,qw,qx,qy,qz\n
                     (손가락 curl 5개 0~1, IMU 쿼터니언 — 이미 tare 기준 상대값)
  Unity → 아두이노:  t                       (IMU 영점 재조절)
                     r                       (손가락 min/max 캘리브레이션 초기화)
                     H,v0,v1,v2,v3,v4        (햅틱 서보 목표값, 0~1000 — Lucas glove 정지각도)
  ```
- Unity에서 **`T`키**를 누르면 자동으로 `t` 명령이 전송됨 (`SerialGloveReceiver.cs`)

---

## 팀원

| 역할 | 이름 | 담당 |
|---|---|---|
| 팀장 | 최호민 | |
| 팀원 | 김동휘 | |
| 팀원 | 권민규 | |
| 팀원 | 배건우 | Unity / 컴퓨터 비전 |
| 팀원 | 이서우 | |

지도교수: 김선용 교수님 | 산업체 멘토: 한화 비전 연구원 김나연
