using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class UltraMovementController : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("Камера от 1-го лица (дочерний объект)")]
    public Transform cameraPivot;

    [Header("Move")]
    public float runSpeed = 16.5f;
    public float crouchSpeed = 8f;
    public float airControl = 0.6f;
    public float gravity = -30f;
    public float jumpHeight = 2f;
    [Range(0f,1f)] public float inputDeadzone = 0.12f;

    [Header("Dash")]
    public KeyCode dashKey = KeyCode.LeftShift;
    public float dashSpeed = 50f;
    public float dashTime = 0.18f;
    public float dashCooldown = 0.08f;

    [Header("Slide / Crouch")]
    public KeyCode crouchKey = KeyCode.LeftControl;
    public float slideSpeed = 24f;
    public float slideFriction = 10f;
    public float slideMinTime = 0.18f;
    [Tooltip("Опускание камеры при слайде")]
    public float slideCamYOffset = -0.45f;
    [Tooltip("Опускание камеры при приседе")]
    public float crouchCamYOffset = -0.35f;
    public float camLerp = 12f;
    [Tooltip("Высота контроллера в приседе")]
    public float crouchHeight = 1.2f;

    [Header("Slam")]
    public float slamSpeed = 100f;

    [Header("Wall Cling / Jump")]
    public LayerMask wallLayer;               // ← назначь слой стен здесь
    public float wallCheckRadius = 0.4f;
    public float wallCheckDistance = 0.6f;
    public float wallClingFallSpeed = -5f;
    public int   maxWallJumps = 3;
    public float wallJumpHorizontal = 12f;
    public float wallJumpHeight = 2f;
    [Tooltip("Макс. вертикал. составляющая нормали, чтобы считать поверхность стеной")]
    [Range(0f,1f)] public float wallMinDot = 0.2f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 100f;
    public float pitchMin = -90f;
    public float pitchMax = 90f;

    // internals
    CharacterController cc;
    Vector3 vel;                // хранит вертикаль
    Vector3 dashVel;            // добавка рывка
    Vector3 slideVel;           // скорость слайда
    float dashT, dashCD;
    bool isSliding;
    bool isCrouching;
    float slideT;
    float baseCamY;
    float pitch;
    bool grounded;
    int wallJumpsLeft;
    Vector3 lastWallNormal;
    bool wallTouch;
    float defaultHeight;
    Vector3 defaultCenter;

    void Start()
    {
        cc = GetComponent<CharacterController>();
        if (!cameraPivot) { Debug.LogError("Assign cameraPivot"); enabled = false; return; }

        baseCamY = cameraPivot.localPosition.y;
        defaultHeight = cc.height;
        defaultCenter = cc.center;

        Cursor.lockState = CursorLockMode.Locked; // см. доки по lockState
        Cursor.visible = false;                   // курсор скрыт в Locked. :contentReference[oaicite:1]{index=1}

        wallJumpsLeft = maxWallJumps;
    }

    void Update()
    {
        // Mouse look
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            float mx = Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
            float my = Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;
            pitch = Mathf.Clamp(pitch - my, pitchMin, pitchMax);
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            transform.Rotate(Vector3.up * mx, Space.Self);
        }

        grounded = cc.isGrounded;
        if (grounded && vel.y < 0f) { vel.y = -2f; wallJumpsLeft = maxWallJumps; }

        // Input
        float ix = Input.GetAxisRaw("Horizontal");
        float iz = Input.GetAxisRaw("Vertical");
        Vector2 raw = new Vector2(ix, iz);
        float inputMag = Mathf.Clamp01(raw.magnitude);
        bool hasMove = inputMag > inputDeadzone;
        Vector3 wishDir = (transform.right * ix + transform.forward * iz).normalized;

        // Crouch/Slide state machine
        if (grounded)
        {
            // начало слайда только в движении
            if (!isSliding && Input.GetKeyDown(crouchKey) && hasMove)
                StartSlide(wishDir);

            // начало приседа при стоянии на месте
            if (!isSliding && Input.GetKey(crouchKey) && !hasMove)
                SetCrouch(true);

            // если держим ctrl во время бега — начнётся слайд,
            // по окончании — останемся в приседе, если ctrl ещё зажат.
        }

        // выход из приседа при отпускании ctrl (если не слайдим)
        if (!isSliding && isCrouching && Input.GetKeyUp(crouchKey))
            SetCrouch(false);

        // Jump / Wall jump / Slam
        if (Input.GetButtonDown("Jump"))
        {
            if (grounded) Jump(ref vel, jumpHeight);
            else if (wallTouch && wallJumpsLeft > 0) WallJump(ref vel);
        }
        if (!grounded && Input.GetKeyDown(crouchKey))
            Slam();

        // Dash — разрешён только в движении
        if (Input.GetKeyDown(dashKey) && dashCD <= 0f && hasMove)
            StartDash(wishDir);

        // Horizontal move
        Vector3 horiz = Vector3.zero;

        if (isSliding)
        {
            slideT += Time.deltaTime;
            // трение слайда
            slideVel = Vector3.MoveTowards(slideVel, Vector3.zero, slideFriction * Time.deltaTime);
            horiz += slideVel;

            // переход в присед после слайда, если ctrl держится, иначе выходим
            if (slideT >= slideMinTime && (slideVel.sqrMagnitude < 0.1f || !hasMove))
            {
                EndSlide();
                if (Input.GetKey(crouchKey)) SetCrouch(true);
            }
        }
        else
        {
            float speed = isCrouching ? crouchSpeed : runSpeed;
            float control = grounded ? 1f : airControl;
            horiz += wishDir * (speed * control * inputMag);
        }

        // dash add
        if (dashT > 0f)
        {
            dashT -= Time.deltaTime;
            horiz += dashVel;
            if (dashT <= 0f) dashVel = Vector3.zero;
        }
        if (dashCD > 0f) dashCD -= Time.deltaTime;

        // Wall probe + gravity
        WallProbe(); // обновляет wallTouch/lastWallNormal
        if (!grounded)
        {
            if (wallTouch && vel.y < 0f) vel.y = wallClingFallSpeed; // «прилипание»
            else vel.y += gravity * Time.deltaTime;
        }

        // Move
        Vector3 motion = (horiz + new Vector3(0, vel.y, 0)) * Time.deltaTime;
        cc.Move(motion); // возврат CollisionFlags можно читать при необходимости. :contentReference[oaicite:2]{index=2}

        // Camera Y lerp
        float targetY =
            isSliding ? baseCamY + slideCamYOffset :
            isCrouching ? baseCamY + crouchCamYOffset :
            baseCamY;

        Vector3 cp = cameraPivot.localPosition;
        cp.y = Mathf.Lerp(cp.y, targetY, camLerp * Time.deltaTime);
        cameraPivot.localPosition = cp;
    }

    // --- Actions ---
    void StartDash(Vector3 wishDir)
    {
        // если ввода нет — не дэшить
        if (wishDir.sqrMagnitude < 0.01f) return;
        dashVel = wishDir.normalized * dashSpeed;
        dashT = dashTime;
        dashCD = dashCooldown;
    }

    void StartSlide(Vector3 wishDir)
    {
        isSliding = true;
        slideT = 0f;
        SetCrouch(false); // на время слайда уходим из стат. приседа
        // стартовый импульс по текущему направлению
        slideVel = wishDir.normalized * slideSpeed;
    }

    void EndSlide()
    {
        isSliding = false;
        slideVel = Vector3.zero;
    }

    void SetCrouch(bool state)
    {
        if (isCrouching == state) return;
        isCrouching = state;

        // аккуратно меняем высоту капсулы, не проваливаясь в пол
        if (state)
        {
            cc.height = crouchHeight;
            cc.center = new Vector3(defaultCenter.x, crouchHeight * 0.5f, defaultCenter.z);
        }
        else
        {
            cc.height = defaultHeight;
            cc.center = defaultCenter;
        }
    }

    void Slam() { vel.y = -Mathf.Abs(slamSpeed); }

    void Jump(ref Vector3 v, float height) { v.y = Mathf.Sqrt(height * -2f * gravity); }

    void WallJump(ref Vector3 v)
    {
        wallJumpsLeft--;
        Vector3 horiz = Vector3.ProjectOnPlane(lastWallNormal, Vector3.up).normalized;
        if (horiz.sqrMagnitude < 0.01f) horiz = -transform.forward;

        v.y = Mathf.Sqrt(wallJumpHeight * -2f * gravity);

        // короткий горизонтальный импульс от стены
        dashVel = (-horiz * wallJumpHorizontal);
        dashT = 0.12f;

        EndSlide();
    }

    // --- Wall detection ---
    void WallProbe()
    {
        wallTouch = false;
        lastWallNormal = Vector3.zero;

        Vector3 origin = transform.position + Vector3.up * (cc.height * 0.5f);
        Vector3[] dirs = { transform.forward, -transform.forward, transform.right, -transform.right };

        foreach (var d in dirs)
        {
            if (Physics.SphereCast(origin, wallCheckRadius, d, out RaycastHit hit, wallCheckDistance, wallLayer, QueryTriggerInteraction.Ignore))
            {
                float dotUp = Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up));
                if (dotUp < wallMinDot)
                {
                    wallTouch = true;
                    lastWallNormal = hit.normal;
                    return;
                }
            }
        }
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // боковые касания для cling
        if (((cc.collisionFlags & CollisionFlags.Sides) != 0) && ((wallLayer.value & (1 << hit.collider.gameObject.layer)) != 0))
        {
            float dotUp = Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up));
            if (dotUp < wallMinDot)
            {
                wallTouch = true;
                lastWallNormal = hit.normal;
            }
        }
    }
}
