using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

public class ContinuousMovement : MonoBehaviour
{
    public XRNode inputSource;
    private Vector2 inputAxis;
    private CharacterController character;

    [SerializeField]
    private float speed = 1;

    void Start()
    {
        character.GetComponent<CharacterController>();
    }

    private void FixedUpdate()
    {
        Vector3 direction = new Vector3(inputAxis.x, 0, inputAxis.y);
        character.Move(direction * speed * Time.deltaTime);
    }

    void Update()
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(inputSource);
        device.TryGetFeatureValue(CommonUsages.primary2DAxis, out inputAxis);
    }
}