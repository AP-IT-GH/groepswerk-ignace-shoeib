using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;



public class CollisionController : MonoBehaviour
{
    public Button button;
    private void Start()
    {
        Button btn = button.GetComponent<Button>();
    }
    private void OnCollisionEnter(Collision collision)
    {
       button.onClick?.Invoke();
    }
}
