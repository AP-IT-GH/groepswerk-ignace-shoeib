using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class HandPresence : MonoBehaviour
{
    public InputDeviceCharacteristics characteristics;
    public List<GameObject> controllerPrefabs;
    public GameObject handPrefab;

    private InputDevice targetDevice;
    private GameObject spawnedController;
    private GameObject spawnedHand;
    private Animator handAnimator;
    // Start is called before the first frame update
    void Start()
    {
        TryInitialize();
    }

    void TryInitialize()
    {
        List<InputDevice> devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(characteristics, devices);
        if (devices.Count > 0)
        {
            targetDevice = devices[0];
            spawnedHand = Instantiate(handPrefab, transform);
            handAnimator = spawnedHand.GetComponent<Animator>();
        }
    }

    void UpdateHandAnimation()
    {
        if (targetDevice.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue))
        {
            handAnimator.SetFloat("Trigger", triggerValue);
        }
        else
        {
            handAnimator.SetFloat("Trigger", 0);
        }

        if (targetDevice.TryGetFeatureValue(CommonUsages.grip, out float gripValue))
        {
            handAnimator.SetFloat("Grip", gripValue);
        }
        else
        {
            handAnimator.SetFloat("Grip", 0);
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (!targetDevice.isValid)
        {
            TryInitialize();
        }
        else
        {
            spawnedHand.SetActive(true);
            UpdateHandAnimation();
        }
    }
}
