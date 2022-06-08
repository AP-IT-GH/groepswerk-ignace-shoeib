using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ZeroRotation : MonoBehaviour
{
    void LateUpdate()
    {
        transform.eulerAngles = new Vector3(0, transform.eulerAngles.y, 0);
    }
}
