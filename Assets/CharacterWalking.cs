using UnityEngine;

public class CharacterWalking : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float mouseSensitivity = 2f;
    
    private float verticalRotation = 0f;

    void Start()
    {
        // Lock and hide the cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        // Handle mouse look
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;
        
        // Rotate the camera left-right
        transform.Rotate(Vector3.up * mouseX);
        
        // Rotate the camera up-down
        verticalRotation -= mouseY;
        verticalRotation = Mathf.Clamp(verticalRotation, -90f, 90f);
        transform.localRotation = Quaternion.Euler(verticalRotation, transform.eulerAngles.y, 0f);

        // Modified WASD movement to stay on horizontal plane
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");
        
        Vector3 movement = transform.right * moveX + new Vector3(transform.forward.x, 0, transform.forward.z).normalized * moveZ;
        Vector3 newPosition = transform.position + movement * moveSpeed * Time.deltaTime;
        
        // Keep the original Y position
        newPosition.y = transform.position.y;
        transform.position = newPosition;
    }
}
