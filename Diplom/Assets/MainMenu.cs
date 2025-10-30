using UnityEngine;

public class MenuController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Canvas или корневой объект меню")]
    public GameObject menuUI;
    [Tooltip("Скрипт управления игроком (PlayerController)")]
    public MonoBehaviour playerController;

    [Header("Settings")]
    public KeyCode toggleKey = KeyCode.Escape;

    private bool menuActive = true;

    void Start()
    {
        UpdateState();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            menuActive = !menuActive;
            menuUI.SetActive(menuActive);
            UpdateState();
        }
    }

    void UpdateState()
    {
        if (playerController != null)
            playerController.enabled = !menuActive;

        Cursor.lockState = menuActive ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = menuActive;
    }
}
