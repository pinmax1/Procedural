using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ExampleMetric : MonoBehaviour, IMetric
{
    public Transform center;

    public float Evaluate(Vector3 position)
    {
        if (center == null) return 0f;
        return Vector3.Distance(position, center.position);
    }
}
