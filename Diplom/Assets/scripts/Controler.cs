using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class UltraMovementController : MonoBehaviour
{
    [Header("Refs")]
    public Transform cameraPivot;

    // >>> NEW: Exploration mode <<<
    [Header("Mode")]
    [Tooltip("Режим исследования: без дэша, слэма и слайда; медленная ходьба; присед без слайда")]
    public bool explorationMode = false;
    public float exploreSpeed = 6f;

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
    public float slideStartSpeed = 6f;      // мин. гориз. скорость для старта слайда
    public float slideSpeed = 24f;          // импульс старта слайда
    public float slideFriction = 6f;        // меньше = дольше скользим
    public float slideMinTime = 0.22f;      // минимум держим слайд
    public float slideCamYOffset = -0.45f;
    public float crouchCamYOffset = -0.35f;
    public float camLerp = 12f;
    public float crouchHeight = 1.2f;

    [Header("Slide Cancel")]
    public bool allowSlideCancel = true;
    public bool preserveSlideMomentumOnCancel = true;
    public float slideCancelCarryTime = 0.15f;

    [Header("Crouch Safety")]
    public float standUpExtra = 0.05f;
    public LayerMask headHitMask = ~0;
    public float crouchStepOffset = 0.00f;  // без «подпрыга» в приседе

    [Header("Slam")]
    public float slamSpeed = 100f;

    [Header("Wall Cling / Jump")]
    public LayerMask wallLayer;
    public float wallCheckRadius = 0.4f;
    public float wallCheckDistance = 0.6f;
    public float wallClingFallSpeed = -5f;
    public int   maxWallJumps = 3;
    public float wallJumpHorizontal = 12f;
    public float wallJumpHeight = 2f;
    [Range(0f,1f)] public float wallMinDot = 0.2f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 0.12f;  // RAW, без deltaTime
    public float pitchMin = -90f, pitchMax = 90f;

    // internals
    CharacterController cc;
    Vector3 vel;                 // вертикаль
    Vector3 dashVel;             // добавка рывка
    Vector3 slideVel;            // скорость слайда
    float dashT, dashCD;
    bool isSliding;
    bool isCrouching;
    float slideT;
    float baseCamY;
    float pitch, mouseX, mouseY;
    bool grounded;
    int wallJumpsLeft;
    Vector3 lastWallNormal;
    bool wallTouch;
    float defaultHeight;
    Vector3 defaultCenter;
    float defaultStepOffset;

    void Start()
    {
        cc = GetComponent<CharacterController>();
        if (!cameraPivot) { Debug.LogError("Assign cameraPivot"); enabled = false; return; }

        baseCamY = cameraPivot.localPosition.y;
        defaultHeight = cc.height;
        defaultCenter = cc.center;
        defaultStepOffset = cc.stepOffset;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        wallJumpsLeft = maxWallJumps;
    }

    void Update()
    {
        // --- RAW mouse input (только чтение) ---
        mouseX = Input.GetAxisRaw("Mouse X");
        mouseY = Input.GetAxisRaw("Mouse Y");

        grounded = cc.isGrounded;
        if (grounded && vel.y < 0f) { vel.y = -2f; wallJumpsLeft = maxWallJumps; }

        // Input
        float ix = Input.GetAxisRaw("Horizontal");
        float iz = Input.GetAxisRaw("Vertical");
        Vector2 raw = new Vector2(ix, iz);
        float inputMag = Mathf.Clamp01(raw.magnitude);
        bool hasMove = inputMag > inputDeadzone;
        Vector3 wishDir = (transform.right * ix + transform.forward * iz).normalized;

        // --- Crouch / Slide ---
        bool crouchDown = Input.GetKeyDown(crouchKey);
        bool crouchHeld = Input.GetKey(crouchKey);
        bool crouchUp   = Input.GetKeyUp(crouchKey);

        // Gating по режиму
        bool slideAllowed = !explorationMode;
        bool dashAllowed  = !explorationMode;
        bool slamAllowed  = !explorationMode;

        // старт слайда — только по KeyDown и при достаточной скорости
        if (slideAllowed && grounded && !isSliding && crouchDown && hasMove && CurrentHorizSpeed() >= slideStartSpeed)
            StartSlide(wishDir);

        // удерживаемый присед (в exploration — всегда по удержанию)
        if (grounded && !isSliding)
        {
            if (crouchHeld && (!slideAllowed || !hasMove)) EnsureCrouch();
            if (crouchUp) TryStand();
        }

        // Jump / Wall jump / Slam
        if (Input.GetButtonDown("Jump"))
        {
            if (isSliding && slideAllowed && allowSlideCancel) CancelSlideEarly(); // отмена слайда прыжком
            if (grounded) Jump(ref vel, jumpHeight);
            else if (wallTouch && wallJumpsLeft > 0) WallJump(ref vel);
        }
        if (!grounded && crouchDown && slamAllowed) Slam();

        // Dash — только в движении
        if (dashAllowed && Input.GetKeyDown(dashKey) && dashCD <= 0f && hasMove)
            StartDash(wishDir);

        // Horizontal move
        Vector3 horiz = Vector3.zero;

        if (isSliding)
        {
            // ранний выход из слайда по отпусканию Ctrl
            if (slideAllowed && allowSlideCancel && Input.GetKeyUp(crouchKey))
                CancelSlideEarly();

            slideT += Time.deltaTime;

            // трение слайда
            slideVel = Vector3.MoveTowards(slideVel, Vector3.zero, slideFriction * Time.deltaTime);
            horiz += slideVel;

            // авто-завершение
            if (slideT >= slideMinTime && slideVel.sqrMagnitude < 0.05f)
            {
                EndSlide();
                if (crouchHeld) EnsureCrouch(); else TryStand();
            }
        }
        else
        {
            float baseSpeed = explorationMode ? exploreSpeed : runSpeed;
            float speed = isCrouching ? Mathf.Min(crouchSpeed, baseSpeed) : baseSpeed;
            float control = grounded ? 1f : airControl;
            horiz += wishDir * (speed * control * inputMag);
        }

        // dash add
        if (dashT > 0f)
        {
            dashT -= Time.deltaTime;
            if (dashAllowed) horiz += dashVel;
            if (dashT <= 0f) dashVel = Vector3.zero;
        }
        if (dashCD > 0f) dashCD -= Time.deltaTime;

        // Wall probe + gravity
        WallProbe();
        if (!grounded)
        {
            if (wallTouch && vel.y < 0f) vel.y = wallClingFallSpeed;
            else vel.y += gravity * Time.deltaTime;
        }

        // Move
        Vector3 motion = (horiz + new Vector3(0, vel.y, 0)) * Time.deltaTime;
        cc.Move(motion);

        // Camera Y lerp
        float targetY =
            isSliding ? baseCamY + slideCamYOffset :
            isCrouching ? baseCamY + crouchCamYOffset :
            baseCamY;

        Vector3 cp = cameraPivot.localPosition;
        cp.y = Mathf.Lerp(cp.y, targetY, camLerp * Time.deltaTime);
        cameraPivot.localPosition = cp;
    }

    void LateUpdate()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        // камера
        pitch = Mathf.Clamp(pitch - mouseY * mouseSensitivity, pitchMin, pitchMax);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        // тело — после Move, без deltaTime
        transform.Rotate(0f, mouseX * mouseSensitivity, 0f, Space.Self);
    }

    // --- Actions ---
    void StartDash(Vector3 wishDir)
    {
        if (wishDir.sqrMagnitude < 0.01f) return;
        dashVel = wishDir.normalized * dashSpeed;
        dashT = dashTime;
        dashCD = dashCooldown;
    }

    void StartSlide(Vector3 wishDir)
    {
        isSliding = true;
        slideT = 0f;

        // уменьшаем капсулу для прохода под низкими объектами
        SetCrouchCapsule(true);

        // импульс = max(текущая гориз. скорость, slideSpeed)
        Vector3 cur = HorizontalFrom(cc.velocity);
        float startSpeed = Mathf.Max(cur.magnitude, slideSpeed);
        slideVel = wishDir.normalized * startSpeed;

        // прижать к земле
        vel.y = -2f;
    }

    void EndSlide()
    {
        isSliding = false;
        slideVel = Vector3.zero;
    }

    void CancelSlideEarly()
    {
        if (!isSliding) return;

        Vector3 carry = slideVel;   // запомним остаток импульса
        EndSlide();

        if (!explorationMode && preserveSlideMomentumOnCancel && carry.sqrMagnitude > 0.0001f)
        {
            dashVel = carry;
            dashT = Mathf.Max(dashT, slideCancelCarryTime);
        }

        // отпущен Ctrl — пробуем встать; если низкий потолок — остаёмся присев
        int playerLayer = gameObject.layer;
        int mask = headHitMask & ~(1 << playerLayer);
        if (CanStandFromCurrentFeet(mask)) SetCrouchCapsule(false);
        else EnsureCrouch();
    }

    void EnsureCrouch()
    {
        if (isCrouching) return;
        SetCrouchCapsule(true);
    }

    void TryStand()
    {
        if (!isCrouching) return;

        int playerLayer = gameObject.layer;
        int mask = headHitMask & ~(1 << playerLayer);

        if (CanStandFromCurrentFeet(mask))
            SetCrouchCapsule(false);
    }

    void SetCrouchCapsule(bool crouch)
    {
        if (crouch)
        {
            if (!isCrouching)
            {
                SafeSetHeightKeepingFeet(crouchHeight);
                isCrouching = true;
                cc.stepOffset = crouchStepOffset;   // без подъёма на ступени
                vel.y = Mathf.Min(vel.y, -2f);      // прижим
            }
        }
        else
        {
            if (isCrouching)
            {
                SafeSetHeightKeepingFeet(defaultHeight);
                isCrouching = false;
                cc.stepOffset = defaultStepOffset;
            }
        }
    }

    float CurrentHorizSpeed() => HorizontalFrom(cc.velocity).magnitude;
    static Vector3 HorizontalFrom(Vector3 v) { v.y = 0f; return v; }

    void Slam() { vel.y = -Mathf.Abs(slamSpeed); }

    void Jump(ref Vector3 v, float height) { v.y = Mathf.Sqrt(height * -2f * gravity); }

    void WallJump(ref Vector3 v)
    {
        wallJumpsLeft--;
        Vector3 horiz = Vector3.ProjectOnPlane(lastWallNormal, Vector3.up).normalized;
        if (horiz.sqrMagnitude < 0.01f) horiz = -transform.forward;

        v.y = Mathf.Sqrt(wallJumpHeight * -2f * gravity);

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

    // --- Capsule helpers (режем сверху, без провалов) ---
    void SafeSetHeightKeepingFeet(float newHeight)
    {
        newHeight = Mathf.Max(newHeight, cc.radius * 2f + 0.01f);

        Vector3 bottom = BottomSphereWorld(cc.center, cc.height, cc.radius);

        bool prevDetect = cc.detectCollisions;
        cc.detectCollisions = false;
        cc.height = newHeight;

        float newCenterYLocal =
            (bottom - transform.position).y + (cc.height * 0.5f - cc.radius);

        cc.center = new Vector3(defaultCenter.x, newCenterYLocal, defaultCenter.z);
        cc.detectCollisions = prevDetect;

        cc.Move(Vector3.down * 0.001f);
    }

    bool CanStandFromCurrentFeet(int mask)
    {
        Vector3 bottom = BottomSphereWorld(cc.center, cc.height, cc.radius);
        Vector3 top    = TopSphereWorldFromBottom(bottom, defaultHeight, cc.radius);
        top += Vector3.up * standUpExtra;

        float radius = Mathf.Max(cc.radius - cc.skinWidth, 0.001f);
        return !Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore);
    }

    Vector3 BottomSphereWorld(Vector3 centerLocal, float height, float radius)
    {
        Vector3 worldCenter = transform.position + centerLocal;
        return worldCenter + Vector3.down * (height * 0.5f - radius);
    }

    Vector3 TopSphereWorldFromBottom(Vector3 bottomSphereWorld, float height, float radius)
    {
        return bottomSphereWorld + Vector3.up * (height - 2f * radius);
    }
}
