using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Collection : MonoBehaviour
{
    public GameObject door;
    Door doorComponent;
    // Start is called before the first frame update
    void Start()
    {
        doorComponent = door.GetComponent<Door>();
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.tag == "player")
        {
            doorComponent.collectedAmount++;
            Destroy(gameObject);
        }
    }
}
