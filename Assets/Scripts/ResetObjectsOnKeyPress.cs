// ResetObjectsOnKeyPress.cs
//
// 테스트하다가 물체들을 이리저리 던지고 잡고 하면서 위치가 흐트러졌을 때,
// 지정한 키 한 번으로 처음(Start 시점) 위치/회전으로 되돌리는 개발용 편의 기능.

using UnityEngine;
using Valve.VR.InteractionSystem;

public class ResetObjectsOnKeyPress : MonoBehaviour
{
    [Tooltip("켜두면 씬에 있는 Throwable 붙은 물체를 전부 자동으로 찾아서 리셋 대상으로 씀 (직접 등록 안 해도 됨)")]
    public bool autoFindThrowables = true;

    [Tooltip("자동 탐색을 끈 경우에만 사용됨 - 리셋 대상 물체들을 직접 등록")]
    public Transform[] objectsToReset;

    [Tooltip("이 키를 누르면 등록된 물체들을 전부 초기 상태로 되돌립니다.")]
    public KeyCode resetKey = KeyCode.Backspace;

    private Transform[] targets;
    private Vector3[] initialPositions;
    private Quaternion[] initialRotations;

    void Start()
    {
        if (autoFindThrowables)
        {
            Throwable[] throwables = FindObjectsByType<Throwable>(FindObjectsSortMode.None);
            targets = new Transform[throwables.Length];
            for (int i = 0; i < throwables.Length; i++)
                targets[i] = throwables[i].transform;

            Debug.Log($"[ResetObjectsOnKeyPress] Throwable {targets.Length}개 자동 등록됨");
        }
        else
        {
            targets = objectsToReset;
        }

        initialPositions = new Vector3[targets.Length];
        initialRotations = new Quaternion[targets.Length];

        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i] == null) continue;
            initialPositions[i] = targets[i].position;
            initialRotations[i] = targets[i].rotation;
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(resetKey))
        {
            ResetAll();
        }
    }

    public void ResetAll()
    {
        // 혹시 지금 손에 잡혀있는 물체가 있으면, 리셋 전에 먼저 손에서 놓게 함.
        // 안 그러면 Hand 쪽 상태(currentAttachedObjectInfo)는 여전히 "잡고 있다"고
        // 기억하는데 물체 위치만 갑자기 바뀌어서, 다음 프레임에 손이 그 물체를
        // 다시 자기 위치로 확 끌고 오는 등 어색한 상태가 될 수 있음.
        Hand[] hands = FindObjectsByType<Hand>(FindObjectsSortMode.None);

        for (int i = 0; i < targets.Length; i++)
        {
            Transform obj = targets[i];
            if (obj == null) continue;

            foreach (var hand in hands)
            {
                if (hand.currentAttachedObject == obj.gameObject)
                {
                    hand.DetachObject(obj.gameObject, restoreOriginalParent: true);
                }
            }

            obj.position = initialPositions[i];
            obj.rotation = initialRotations[i];

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        Debug.Log("[ResetObjectsOnKeyPress] 물체들을 초기 상태로 리셋함");
    }
}
