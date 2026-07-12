//======= Custom replacement (SteamVR/OpenVR 의존성 제거 버전) ===============
//
// 원본 Valve SteamVR Interaction System의 Hand.cs를 대체합니다.
// OpenVR/SteamVR 런타임을 전혀 호출하지 않고, 외부 트래킹 파이프라인
// (ArUco 마커 위치, IMU 방향, 포텐셔미터 curl)이 이 컴포넌트의
// public 필드/transform을 매 프레임 갱신해주는 방식으로 동작합니다.
//
// Interactable.cs, Throwable.cs, VelocityEstimator.cs, GrabTypes.cs는
// 원본 그대로이며 이 클래스만 참조합니다 (public API 형태를 최대한 맞춤).
//
// [단순화한 부분]
// - GrabTypes(Trigger/Pinch/Grip 등) 세분화 없이 curl 값 하나(grabCurl)로 grab 판정
// - 컨트롤러 진동(haptics), ShowGrabHint 등 시각 힌트는 no-op
// - 손 스켈레톤 포즈 블렌딩(skeleton.BlendToSkeleton)은 아직 미구현 (추후 grip pose 블렌딩 단계에서 별도 구현 예정)
// - GetEstimatedPeakVelocities는 최근 N프레임 중 최대값으로 근사
//
// [수정 이력]
// - grab 상태 전이(rising/falling edge) 감지를 FixedUpdate()에서 Update()로 이동.
//   원인: 이 프로젝트는 웹캠/OpenCV 처리 부하로 실측 프레임레이트가 기본 물리 주기(50Hz)보다
//   낮아서(실측 27~31fps), 한 렌더 프레임 안에 FixedUpdate가 2번 이상 도는 경우가 흔했음.
//   grabEndedThisFrame/grabStartedThisFrame을 FixedUpdate에서 세팅하고 Update에서 소비하는
//   구조였는데, 같은 프레임 안에서 FixedUpdate가 여러 번 돌면 처음에 true로 세팅된 edge flag가
//   두 번째 FixedUpdate 호출에서 false로 덮어써진 뒤 Update가 실행돼서, release(그리고 이론상
//   grab도) 신호가 소비되기 전에 사라져버리는 문제가 있었음.
//   -> Update()는 렌더 프레임당 정확히 1번만 실행되므로, 계산과 소비를 같은 Update() 호출
//   안에서 처리하도록 옮겨서 해결. FixedUpdate는 물리 timestep이 필요한 속도 추정만 남김.
// - AttachmentFlags.SnapOnAttach를 실제로 처리하는 로직을 AttachObject()에 추가.
//   원인: 이 플래그가 enum엔 정의돼 있었지만 실제로 체크하는 코드가 어디에도 없어서,
//   물체를 잡아도(부모는 손으로 바뀌어도) SetParent()가 월드 좌표를 그대로 유지하는 바람에
//   물체가 잡히기 직전 위치에 계속 남아있는(손에 스냅되지 않는) 문제가 있었음.
//   -> SnapOnAttach 플래그가 켜진 경우, objectAttachmentPoint(또는 attachmentOffset)
//   위치/회전으로 물체를 실제로 이동시키도록 함. 이 플래그는 각 Throwable 컴포넌트의
//   Attachment Flags 드롭다운에서 개별적으로 켜야 적용됨 (기본값엔 안 포함되어 있음).
// - GetTrackedObjectVelocity/AngularVelocity를 순간값 대신 최근 N프레임(velocityHistory)
//   평균으로 변경. 원인: 웹캠/ArUco 트래킹 노이즈가 순간 속도 계산에 그대로 반영돼서,
//   물체를 놓는 그 한 프레임에 노이즈가 튀면 의도보다 훨씬 세게 던져지는 문제가 있었음.
//
//=============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Valve.VR.InteractionSystem
{
    // Throwable.cs의 onHeldUpdate 등에서 사용하는 이벤트 타입 (원본 Hand.cs에 있던 정의)
    public class HandEvent : UnityEvent<Hand> { }

    public class Hand : MonoBehaviour
    {
        [Flags]
        public enum AttachmentFlags
        {
            SnapOnAttach = 1 << 0,
            DetachOthers = 1 << 1,
            DetachFromOtherHand = 1 << 2,
            ParentToHand = 1 << 3,
            VelocityMovement = 1 << 4,
            TurnOnKinematic = 1 << 5,
            TurnOffGravity = 1 << 6,
            AllowSidegrade = 1 << 7,
        };

        public const AttachmentFlags defaultAttachmentFlags = AttachmentFlags.ParentToHand |
                                                              AttachmentFlags.DetachOthers |
                                                              AttachmentFlags.DetachFromOtherHand |
                                                              AttachmentFlags.TurnOnKinematic;

        public struct AttachedObject
        {
            public GameObject attachedObject;
            public Interactable interactable;
            public Rigidbody attachedRigidbody;
            public AttachmentFlags attachmentFlags;
            public Transform originalParent;
            public bool attachedRigidbodyWasKinematic;
            public bool attachedRigidbodyUsedGravity;
            public Transform handAttachmentPointTransform;
            public GrabTypes grabbedWithType;
        }

        [Header("Hover 감지 설정")]
        [Tooltip("이 반경 안의 Interactable을 hover 대상으로 감지합니다.")]
        public float hoverRadius = 0.1f;
        public LayerMask hoverLayerMask = ~0;

        [Header("Grab 입력 (포텐셔미터 curl로부터 채워짐)")]
        [Range(0f, 1f)]
        [Tooltip("0=편 손, 1=완전히 굽힘. 아두이노에서 온 curl 값을 여기에 매 프레임 대입하세요.")]
        public float grabCurl = 0f;
        [Range(0f, 1f)]
        public float grabThreshold = 0.7f;

        public Hand otherHand;
        public bool noSteamVRFallbackCamera = false;
        [Tooltip("왼손/오른손 구분. SteamVR_ActionSet 등 일부 필드의 타입 호환을 위해 남겨둔 값이며, 지금 단계에선 실제 동작에 영향 없음")]
        public SteamVR_Input_Sources handType = SteamVR_Input_Sources.LeftHand;

        [Tooltip("트래킹이 유효한지 여부. 나중에 ArUco 마커가 가려지는 등 트래킹 유실 시 이 값을 false로 바꿔주면, 이 값을 참조하는 하위 기능들(HapticRack 등)이 자동으로 멈춥니다.")]
        public bool isActive = true;

        [Tooltip("grab 판정의 기준이 되는 지점. 지정 안 하면 이 오브젝트 자신의 transform을 씁니다.")]
        public Transform hoverSphereTransform;
        public Transform objectAttachmentPoint;

        public Interactable hoveringInteractable => hoveringInteractables.Count > 0 ? hoveringInteractables[0] : null;
        public GameObject currentAttachedObject => currentAttachedObjectInfo.HasValue ? currentAttachedObjectInfo.Value.attachedObject : null;

        public AttachedObject? currentAttachedObjectInfo = null;

        protected List<Interactable> hoveringInteractables = new List<Interactable>();
        protected Interactable hoverLockedInteractable;
        protected bool hoverLocked = false;

        protected bool grabbedLastFrame = false;
        protected bool grabStartedThisFrame = false;
        protected bool grabEndedThisFrame = false;

        // ---- 속도 추정 (손 자체의 순간 속도. Throwable의 GetFromHand/AdvancedEstimation에서 사용) ----
        private Vector3 prevPosition;
        private Quaternion prevRotation;
        private Vector3 currentVelocity;
        private Vector3 currentAngularVelocity;
        private const int VelocityHistorySize = 8;
        private readonly Queue<Vector3> velocityHistory = new Queue<Vector3>();
        private readonly Queue<Vector3> angularVelocityHistory = new Queue<Vector3>();

        // ---- 스켈레톤 포즈 블렌딩 스텁 (추후 grip pose 블렌딩 구현 시 교체) ----
        public class SkeletonStub
        {
            public void BlendToSkeleton(float blendOverSeconds = 0.1f)
            {
                // TODO: procedural grip pose 블렌딩 로직으로 대체 예정
            }

            public void BlendToPoser(SteamVR_Skeleton_Poser poser, float blendOverSeconds = 0.1f)
            {
                // TODO: 오브젝트별 grip pose 블렌딩 로직으로 대체 예정 (로드맵 5단계)
            }
        }
        public SkeletonStub skeleton = new SkeletonStub();
        public bool HasSkeleton() => false;

        protected virtual void Awake()
        {
            if (hoverSphereTransform == null) hoverSphereTransform = this.transform;
            if (objectAttachmentPoint == null) objectAttachmentPoint = this.transform;

            prevPosition = transform.position;
            prevRotation = transform.rotation;
        }

        protected virtual void Update()
        {
            if (!isActive) return; // 트래킹이 유효하지 않으면(예: ArUco 마커가 가려짐) 상태 갱신을 멈춤

            // grab 상태 전이(rising/falling edge) 감지 - 반드시 Update()에서, 소비하는 코드와
            // 같은 호출 안에서 계산해야 함. FixedUpdate에서 계산하면 한 렌더 프레임 안에
            // FixedUpdate가 여러 번 도는 경우 edge flag가 소비되기 전에 덮어써질 수 있음.
            bool grabbedNow = grabCurl >= grabThreshold;
            grabStartedThisFrame = grabbedNow && !grabbedLastFrame;
            grabEndedThisFrame = !grabbedNow && grabbedLastFrame;
            grabbedLastFrame = grabbedNow;

            UpdateHovering();

            // hover 중인 오브젝트들에게 매 프레임 업데이트 이벤트 전달
            // (Throwable.HandHoverUpdate가 이 안에서 GetGrabStarting()을 폴링해 AttachObject를 직접 호출함)
            for (int i = 0; i < hoveringInteractables.Count; i++)
            {
                hoveringInteractables[i].SendMessage("HandHoverUpdate", this, SendMessageOptions.DontRequireReceiver);
            }

            // 잡고 있는 오브젝트에게 업데이트 이벤트 전달
            // (Throwable.HandAttachedUpdate가 이 안에서 IsGrabEnding()을 폴링해 DetachObject를 직접 호출함)
            if (currentAttachedObjectInfo.HasValue && currentAttachedObjectInfo.Value.interactable != null)
            {
                currentAttachedObjectInfo.Value.interactable.SendMessage("HandAttachedUpdate", this, SendMessageOptions.DontRequireReceiver);
            }
        }

        protected virtual void FixedUpdate()
        {
            // 손 자체 속도 추정 (finite difference) - 물리 timestep이 필요하므로 여기 유지
            float dt = Time.fixedDeltaTime;
            if (dt > 0f)
            {
                Vector3 vel = (transform.position - prevPosition) / dt;

                Quaternion deltaRot = transform.rotation * Quaternion.Inverse(prevRotation);
                deltaRot.ToAngleAxis(out float angleDeg, out Vector3 axis);
                if (angleDeg > 180f) angleDeg -= 360f;
                if (float.IsNaN(axis.x)) axis = Vector3.zero; // 회전이 거의 없을 때 축이 NaN이 되는 것 방지
                Vector3 angVel = axis * (angleDeg * Mathf.Deg2Rad / dt);

                currentVelocity = vel;
                currentAngularVelocity = angVel;

                velocityHistory.Enqueue(vel);
                angularVelocityHistory.Enqueue(angVel);
                if (velocityHistory.Count > VelocityHistorySize) velocityHistory.Dequeue();
                if (angularVelocityHistory.Count > VelocityHistorySize) angularVelocityHistory.Dequeue();
            }

            prevPosition = transform.position;
            prevRotation = transform.rotation;
        }

        protected virtual void UpdateHovering()
        {
            if (hoverLocked)
                return; // 뭔가를 잡고 있는 동안은 새로운 hover 대상을 갱신하지 않음

            Collider[] hits = Physics.OverlapSphere(hoverSphereTransform.position, hoverRadius, hoverLayerMask, QueryTriggerInteraction.Collide);

            List<Interactable> currentlyDetected = new List<Interactable>();
            foreach (var col in hits)
            {
                Interactable interactable = col.GetComponentInParent<Interactable>();
                if (interactable != null && !currentlyDetected.Contains(interactable))
                    currentlyDetected.Add(interactable);
            }

            // 새로 hover 시작한 것들
            foreach (var interactable in currentlyDetected)
            {
                if (!hoveringInteractables.Contains(interactable))
                {
                    hoveringInteractables.Add(interactable);
                    interactable.SendMessage("OnHandHoverBegin", this, SendMessageOptions.DontRequireReceiver);
                }
            }

            // hover 벗어난 것들
            for (int i = hoveringInteractables.Count - 1; i >= 0; i--)
            {
                if (!currentlyDetected.Contains(hoveringInteractables[i]))
                {
                    Interactable interactable = hoveringInteractables[i];
                    hoveringInteractables.RemoveAt(i);
                    interactable.SendMessage("OnHandHoverEnd", this, SendMessageOptions.DontRequireReceiver);
                }
            }
        }

        // ------------------- Throwable.cs / Interactable.cs가 사용하는 public API -------------------

        public GrabTypes GetGrabStarting(GrabTypes explicitType = GrabTypes.None)
        {
            return grabStartedThisFrame ? GrabTypes.Grip : GrabTypes.None;
        }

        public GrabTypes GetBestGrabbingType()
        {
            return GetBestGrabbingType(GrabTypes.Grip, false);
        }

        public GrabTypes GetBestGrabbingType(GrabTypes preferred, bool forcePreference = false)
        {
            return (grabCurl >= grabThreshold) ? GrabTypes.Grip : GrabTypes.None;
        }

        public bool IsGrabEnding(GameObject attachedObject)
        {
            if (!grabEndedThisFrame)
                return false;

            if (currentAttachedObjectInfo.HasValue)
                return currentAttachedObjectInfo.Value.attachedObject == attachedObject;

            return false;
        }

        public void AttachObject(GameObject objectToAttach, GrabTypes grabbedWithType, AttachmentFlags flags = defaultAttachmentFlags, Transform attachmentOffset = null)
        {
            // 이미 다른 걸 잡고 있으면 먼저 놓기
            if (currentAttachedObjectInfo.HasValue && (flags & AttachmentFlags.DetachOthers) != 0)
            {
                DetachObject(currentAttachedObjectInfo.Value.attachedObject);
            }

            // 반대쪽 손이 이미 잡고 있으면 그쪽에서 떼기
            if (otherHand != null && (flags & AttachmentFlags.DetachFromOtherHand) != 0)
            {
                if (otherHand.currentAttachedObjectInfo.HasValue && otherHand.currentAttachedObjectInfo.Value.attachedObject == objectToAttach)
                {
                    otherHand.DetachObject(objectToAttach);
                }
            }

            Rigidbody rb = objectToAttach.GetComponent<Rigidbody>();
            Interactable interactable = objectToAttach.GetComponent<Interactable>();

            AttachedObject attached = new AttachedObject
            {
                attachedObject = objectToAttach,
                interactable = interactable,
                attachedRigidbody = rb,
                attachmentFlags = flags,
                originalParent = objectToAttach.transform.parent,
                handAttachmentPointTransform = attachmentOffset != null ? attachmentOffset : this.transform,
                grabbedWithType = grabbedWithType,
            };

            if (rb != null)
            {
                attached.attachedRigidbodyWasKinematic = rb.isKinematic;
                attached.attachedRigidbodyUsedGravity = rb.useGravity;

                if ((flags & AttachmentFlags.TurnOnKinematic) != 0)
                    rb.isKinematic = true;
                if ((flags & AttachmentFlags.TurnOffGravity) != 0)
                    rb.useGravity = false;
            }

            if ((flags & AttachmentFlags.ParentToHand) != 0)
            {
                objectToAttach.transform.SetParent(this.transform);
            }

            // SnapOnAttach: 물체를 손의 grip 지점(objectAttachmentPoint)으로 실제로 이동시킴.
            // SetParent()는 기본적으로 월드 좌표를 그대로 유지하기 때문에, 이 처리가 없으면
            // 부모만 손으로 바뀔 뿐 물체는 잡히기 직전 위치에 계속 남아있게 됨.
            if ((flags & AttachmentFlags.SnapOnAttach) != 0)
            {
                Transform snapReference = attachmentOffset != null ? attachmentOffset : objectAttachmentPoint;

                if (attachmentOffset != null)
                {
                    // attachmentOffset은 "물체 쪽"에서 grip 기준점 역할을 하는 트랜스폼.
                    // 이 기준점이 손의 objectAttachmentPoint와 정확히 겹치도록 물체 전체를 이동/회전시킴.
                    Quaternion rotationDiff = objectAttachmentPoint.rotation * Quaternion.Inverse(attachmentOffset.rotation);
                    objectToAttach.transform.rotation = rotationDiff * objectToAttach.transform.rotation;

                    Vector3 positionDiff = objectAttachmentPoint.position - attachmentOffset.position;
                    objectToAttach.transform.position += positionDiff;
                }
                else
                {
                    objectToAttach.transform.position = objectAttachmentPoint.position;
                    objectToAttach.transform.rotation = objectAttachmentPoint.rotation;
                }
            }

            currentAttachedObjectInfo = attached;
            hoverLocked = true;

            if (interactable != null)
                interactable.SendMessage("OnAttachedToHand", this, SendMessageOptions.DontRequireReceiver);
        }

        public void DetachObject(GameObject objectToDetach, bool restoreOriginalParent = true)
        {
            if (!currentAttachedObjectInfo.HasValue || currentAttachedObjectInfo.Value.attachedObject != objectToDetach)
                return;

            AttachedObject attached = currentAttachedObjectInfo.Value;

            if ((attached.attachmentFlags & AttachmentFlags.ParentToHand) != 0 && restoreOriginalParent)
            {
                objectToDetach.transform.SetParent(attached.originalParent);
            }

            if (attached.attachedRigidbody != null)
            {
                attached.attachedRigidbody.isKinematic = attached.attachedRigidbodyWasKinematic;
                attached.attachedRigidbody.useGravity = attached.attachedRigidbodyUsedGravity;
            }

            currentAttachedObjectInfo = null;
            hoverLocked = false;

            if (attached.interactable != null)
                attached.interactable.SendMessage("OnDetachedFromHand", this, SendMessageOptions.DontRequireReceiver);
        }

        public void HoverLock(Interactable interactable)
        {
            hoverLockedInteractable = interactable;
            hoverLocked = true;
        }

        public void HoverUnlock(Interactable interactable)
        {
            if (hoverLockedInteractable == interactable)
            {
                hoverLockedInteractable = null;
                hoverLocked = false;
            }
        }

        public void ForceHoverUnlock()
        {
            if (currentAttachedObjectInfo.HasValue)
                DetachObject(currentAttachedObjectInfo.Value.attachedObject);

            hoverLockedInteractable = null;
            hoverLocked = false;
        }

        public bool IsGrabbingWithType(GrabTypes type)
        {
            if (type == GrabTypes.None)
                return grabCurl < grabThreshold;

            return grabCurl >= grabThreshold; // 지금은 grab 종류를 세분화하지 않으므로 Grip 하나로 취급
        }

        // ------------------- 햅틱 출력 지점 -------------------
        // 원래 SteamVR에서는 이 함수가 컨트롤러의 진동 모터를 울렸습니다.
        // 지금은 실제 출력 장치가 없으니 이벤트만 발생시키고 로그를 남깁니다.
        // 나중에 서보모터로 촉감 피드백을 줄 때, 이 이벤트를 구독해서
        // Arduino/ESP32로 시리얼 명령을 보내는 스크립트를 붙이면 됩니다.
        public event Action<float> onHapticPulseRequested; // 인자: 0~1로 정규화된 세기

        public void TriggerHapticPulse(ushort durationMicroSec, ushort pulseHz = 0, float amplitude = 1f)
        {
            if (!isActive) return;

            float normalizedIntensity = Mathf.Clamp01(durationMicroSec / 1000f) * Mathf.Clamp01(amplitude);
            onHapticPulseRequested?.Invoke(normalizedIntensity);
        }

        // 원본 SteamVR Hand.cs에 있던 두 번째 오버로드 (초 단위 duration + 주파수 + 진폭)
        public void TriggerHapticPulse(float duration, float frequency, float amplitude)
        {
            if (!isActive) return;

            float normalizedIntensity = Mathf.Clamp01(amplitude);
            onHapticPulseRequested?.Invoke(normalizedIntensity);
        }

        public void ShowGrabHint() { }
        public void ShowGrabHint(string text) { }
        public void HideGrabHint() { }

        public Vector3 GetTrackedObjectVelocity(float timeOffset = 0)
        {
            // 순간(raw) 속도 대신 최근 몇 프레임 평균을 써서, 트래킹 노이즈 때문에
            // 놓는 순간 속도가 비정상적으로 튀는 것을 완화함
            if (velocityHistory.Count == 0) return currentVelocity;
            Vector3 sum = Vector3.zero;
            foreach (var v in velocityHistory) sum += v;
            return sum / velocityHistory.Count;
        }

        public Vector3 GetTrackedObjectAngularVelocity(float timeOffset = 0)
        {
            if (angularVelocityHistory.Count == 0) return currentAngularVelocity;
            Vector3 sum = Vector3.zero;
            foreach (var v in angularVelocityHistory) sum += v;
            return sum / angularVelocityHistory.Count;
        }

        public void GetEstimatedPeakVelocities(out Vector3 velocity, out Vector3 angularVelocity)
        {
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;

            float bestMagSq = -1f;
            foreach (var v in velocityHistory)
            {
                float m = v.sqrMagnitude;
                if (m > bestMagSq)
                {
                    bestMagSq = m;
                    velocity = v;
                }
            }

            float bestAngMagSq = -1f;
            foreach (var v in angularVelocityHistory)
            {
                float m = v.sqrMagnitude;
                if (m > bestAngMagSq)
                {
                    bestAngMagSq = m;
                    angularVelocity = v;
                }
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.3f);
            Gizmos.DrawSphere(transform.position, hoverRadius);
        }
    }
}
