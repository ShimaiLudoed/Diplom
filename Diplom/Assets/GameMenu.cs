using UnityEngine;

public class PauseMenu : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Canvas или пустой объект с меню")]
    [SerializeField] private GameObject pauseMenuUI;

    [Header("Input")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Tab;

    [Header("Player Control (опционально)")]
    [Tooltip("Скрипт управления игроком, который надо отключать на паузе")]
    [SerializeField] private MonoBehaviour playerController;
    
    private bool isPaused = false;

    private void Start()
    {
        // На старте меню должно быть скрыто
        if (pauseMenuUI != null)
            pauseMenuUI.SetActive(false);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (isPaused)
                Resume();
            else
                Pause();
        }
    }

    private void Pause()
    {
        // включаем меню
        if (pauseMenuUI != null)
            pauseMenuUI.SetActive(true);

        // стопаем время
        Time.timeScale = 0f;

        // отключаем управление
        if (playerController != null)
            playerController.enabled = false;

        // можно скрыть курсор, если надо наоборот покажи
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        isPaused = true;
    }

    private void Resume()
    {
        // выключаем меню
        if (pauseMenuUI != null)
            pauseMenuUI.SetActive(false);

        // возвращаем время
        Time.timeScale = 1f;

        // включаем управление
        if (playerController != null)
            playerController.enabled = true;

        // возвращаем курсор в режим игры
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        isPaused = false;
    }
}
