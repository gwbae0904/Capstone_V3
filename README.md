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
[물체 grab/throw]      [서보모터 (햅틱 피드백, 프로토콜 준비됨/연결은 예정)]
```

- **위치**: 손 여러 면(손등/손바닥/손날 등)에 AprilTag(36h11) 마커를 붙이고, 웹캠으로 인식 → 위치만 담당
- **회전**: MPU6050(IMU)이 전담 (ArUco는 위치만, 서로 역할 분리됨 — 안 그러면 충돌함)
- **손가락 굽힘**: 아두이노에 연결된 포텐셔미터 5개(엄지~소지) 값을 시리얼로 수신, Valve 레퍼런스 포즈(편손↔주먹) 블렌딩으로 자연스럽게 표현
- **잡기**: 물체를 잡으면 그 물체에 맞는 손모양(SphereGrab 등)으로 자동 전환, 놓으면 다시 curl 반영으로 복귀
- **햅틱**: 손가락별 서보모터로 힘 피드백 (프로토콜은 준비됨, 게임 로직 연결은 예정)

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

### 5. Play 눌러서 웹캠/마커 인식 확인
`DotTracker`(또는 `ArucoHandTracker`가 붙은 오브젝트) 선택 → Inspector에서 웹캠이 잡히는지,
마커를 비췄을 때 인식되는지 확인.

---

## 마커 준비 (팀원 각자 인쇄해서 테스트할 때)

- **딕셔너리**: `AprilTag 36h11` (ArUco 4x4가 아님, 반드시 이걸로)
- **크기**: 웹캠-손 거리 30~40cm 기준 **2cm × 2cm** 권장 (공간 부족하면 1.2~1.5cm까지 가능)
- **생성 사이트**: [chev.me/arucogen](https://chev.me/arucogen) 등에서 Dictionary를 AprilTag 36h11로 선택
- 각 마커 ID는 `ArucoHandTracker`의 `Markers` 리스트에 등록된 ID(0, 1, 2, 3...)와 정확히 일치해야 함
- **마커를 실제로 어디에 붙였는지(손목, 손등 등)에 따라 `HandVisual`의 `Target Bone Name`을 맞춰야 함** (아래 스크립트 표 참고)

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
                     H,v0,v1,v2,v3,v4        (햅틱 서보 목표값, 0~1000)
  ```
- Unity에서 **`T`키**를 누르면 자동으로 `t` 명령이 전송됨 (`SerialGloveReceiver.cs`)

---

## 주요 스크립트 (`Assets/Scripts/`)

| 파일 | 역할 |
|---|---|
| `ArucoHandTracker.cs` | 웹캠/영상파일 입력 → ArUco/AprilTag 인식 → **위치만** 계산 → 칼만 필터 → RightHand에 적용 |
| `SerialGloveReceiver.cs` | 아두이노 시리얼 수신(curl+IMU), **회전** 적용, tare/햅틱 명령 전송. `Axis Mapping`/`Invert X,Y,Z`로 축 보정 |
| `FingerCurlAnimator.cs` | curl 값으로 장갑 모델의 손가락 뼈를 실제로 굽힘 (Valve 레퍼런스 포즈 `ReferencePose_OpenHand`↔`ReferencePose_Fist` 블렌딩). 물체를 잡는 동안은 자동으로 손을 떼고 grasp pose 애니메이터에게 자세를 맡김 |
| `GraspPoseTrigger.cs` | 물체를 잡으면 그 물체에 맞는 손모양(`Rest`/`SphereGrab`/`StickGrab`/`pinchGrab`)으로 `Animator.Play()`로 즉시 전환 |
| `KeyboardHandDriver.cs` | 하드웨어 없이 WASD+Space로 테스트할 때 사용 |
| `FallbackCameraController.cs` | 헤드셋 없이 WASD+마우스 우클릭으로 시점 조작 (개발용) |

### `Assets/SteamVR/InteractionSystem/Core/Scripts/`에서 수정한 것들

| 파일 | 수정 내용 |
|---|---|
| `Hand.cs` | grab 판정을 FixedUpdate→Update로 이동(저프레임 대응). `SnapOnAttach` 기본 활성화 + `attachmentOffset` 고려한 정교한 스냅 정렬. `GetTrackedObjectVelocity/AngularVelocity`를 순간값 대신 최근 프레임 평균으로 스무딩(놓을 때 이상한 속도 방지) |
| `HandVisual.cs` | 장갑 3D 모델을 인스턴스화하고, **지정한 뼈(`Target Bone Name`)가 항상 RightHand 원점에 오도록 매 프레임 재정렬** — 이게 "마커 위치 = 가상 손의 어느 부위에 대응되는지"를 결정하는 부분. `Visual Rotation Offset Euler`로 모델의 기본 조형 각도도 보정 가능 |

### 개인별로 맞춰야 하는 값 (Inspector에서, 커밋 전에 되돌리기)

- `SerialGloveReceiver` → `Port Name` : 본인 PC의 COM 포트 번호로
- `ArucoHandTracker` → `Fx`/`Fy`/`Distance Scale Correction` : 웹캠마다 다름, 실측 거리로 보정
- `SerialGloveReceiver` → `Axis Mapping`/`Invert X,Y,Z` : MPU6050 부착 방향에 따라 다름 (현재 값: `Axis Mapping = YZX`, `Invert Y`, `Invert Z` 켜짐 — 이건 지금 쓰는 실물 기준이라 다른 보드로 바꾸면 재조정 필요)
- `HandVisual` → `Target Bone Name`/`Additional Offset` : 마커를 실제로 붙인 위치에 맞춰서
- `HandVisual` → `Visual Rotation Offset Euler` : 모델 기본 각도가 이상하면(예: 악수하는 모양) 조정 (현재 값: `X:0, Y:-45, Z:90`)

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
- **잡을 때 물체가 손 위치로 스냅 안 되고 공중에 뜬 채로 따라오는 문제**: `Hand.AttachObject`가 원래
  `SetParent`만 하고 위치를 안 맞춰줘서 생김. `SnapOnAttach` 플래그를 기본값에 포함시키고, 실제로
  정렬하는 로직을 추가해서 해결
- **`HandVisual`의 뼈 정렬을 `Awake()`에서 한 번만 계산하면 안 됨** — 그 시점의 회전 상태에 따라
  나중에 회전축이 엉뚱한 곳(엄지 등)으로 보일 수 있음. 반드시 `LateUpdate`에서 매 프레임 재계산할 것
- **Unity 6 + OpenCvSharp에서 API 이름이 계속 다름**: `PredefinedDictionaryType`, `DetectorParameters`,
  `SolvePnPMethod.IPPE_SQUARE` 등 확실하지 않으면 IDE 자동완성으로 확인하는 게 제일 빠름
- **`SerialPort.ReadExisting()`이 Unity(Mono)에서 가끔 에러를 던지는 알려진 버그** — `BytesToRead` +
  `Read(buffer, offset, count)`로 직접 고정 바이트 버퍼를 읽는 방식으로 대체함

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
