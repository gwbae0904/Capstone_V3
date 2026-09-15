// PrimitiveSizeSetter.cs
//
// Unity 기본 프리미티브 메시(Cube, Sphere)는 Scale = 1일 때 정확히
// "정육면체는 한 변 1미터", "구는 지름 1미터"가 되도록 만들어져 있습니다.
// 그래서 원하는 실제 크기(미터)를 그대로 로컬 스케일 값으로 넣기만 하면 됩니다.
//
// 사용법: ThrowableCube/ThrowableBall 프리팹 안의 실제 메시 오브젝트
// (Cube, Sphere - Scale이 지금 0.1로 되어있는 그 자식 오브젝트)에 붙이고,
// Size In Meters에 원하는 크기를 입력하세요.
// - 정육면체(Cube)라면: 한 변의 길이
// - 구(Sphere)라면: 지름
//
// [ExecuteAlways]가 붙어있어서, Play를 안 눌러도 Inspector에서 값을 바꾸는 즉시
// Scene 뷰에서 바로 크기가 바뀌는 걸 확인할 수 있습니다 (에디터 편집 중에도 동작).

using UnityEngine;

[ExecuteAlways]
public class PrimitiveSizeSetter : MonoBehaviour
{
    [Tooltip("Unity 기본 정육면체(Cube)에 붙였다면 '한 변의 길이'를, 기본 구(Sphere)에 붙였다면 " +
             "'지름'을 미터 단위로 입력하세요. (Unity 기본 프리미티브 메시 기준이며, 커스텀 메시를 " +
             "쓰는 경우 이 값이 정확하지 않을 수 있습니다)")]
    public float sizeInMeters = 0.1f;

    void OnValidate()
    {
        ApplySize();
    }

    void Awake()
    {
        ApplySize();
    }

    private void ApplySize()
    {
        transform.localScale = Vector3.one * sizeInMeters;
    }
}
