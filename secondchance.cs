using System;
using System.Collections.Generic;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;
using Duckov;        // CharacterMainControl, Health
using HarmonyLib;   // Harmony 패치

namespace secondchance
{
    // Duckov 모드 로더 엔트리
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private static Harmony _harmony;

        protected override void OnAfterSetup()
        {
            try
            {
                Debug.Log("[SecondChance] OnAfterSetup - Manager + Harmony patch");

                GameObject root = new GameObject("SecondChanceRoot_UI");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.AddComponent<SecondChanceManager>();

                if (_harmony == null)
                {
                    _harmony = new Harmony("soul.soda.secondchance");
                    _harmony.PatchAll();
                    Debug.Log("[SecondChance] Harmony PatchAll 완료");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] OnAfterSetup 예외: " + ex);
            }
        }

        protected override void OnBeforeDeactivate()
        {
            Debug.Log("[SecondChance] OnBeforeDeactivate - 언로드 (언패치는 생략)");
        }
    }

    // ─────────────────────────────────────────────
    // Health.CurrentHealth setter 패치만 사용
    // ─────────────────────────────────────────────
    [HarmonyPatch(typeof(Health), "set_CurrentHealth")]
    public static class SecondChance_HealthSetCurrentHealthPatch
    {
        static void Prefix(Health __instance, ref float value)
        {
            SecondChanceManager.OnHealthSetCurrentHealthPrefix(__instance, ref value);
        }
    }

    // ─────────────────────────────────────────────
    // 본체 매니저
    // ─────────────────────────────────────────────
    public class SecondChanceManager : MonoBehaviour
    {
        private static SecondChanceManager _instance;

        private CharacterMainControl _player;
        private Health _playerHealth;

        private bool _isDowned;
        private float _downTimer;

        private bool _secondChanceUsed;
        private int _playerInstanceId = -1;
        private float _playerMaxObservedHealth;
        private float _downStartHealth;

        // ★ 빈사(세컨드 찬스) 유지 시간 10초
        private const float DownDuration = 10f;
        private const float MinDownHealth = 1f;      // 다운 중 최소 HP
        private const float DownHpPercent = 0.99f;   // 세컨드 찬스 HP = 최대의 99%

        private MethodInfo _healthKillMethod;
        private PropertyInfo _healthIsDeadProperty;
        private FieldInfo[] _playerDeadFlagFields;

        private GUIStyle _downLabelStyle;
        private Texture2D _grayOverlayTex;

        private bool _hpUiHidden;
        private readonly List<GameObject> _hpUiObjects = new List<GameObject>();

        // ─────────────────────────────────────────────
        // 초기화
        // ─────────────────────────────────────────────
        private void Awake()
        {
            if (_instance != null)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            CacheHealthReflection();
            CreateGrayOverlayTexture();
        }

        private void CacheHealthReflection()
        {
            try
            {
                Type healthType = typeof(Health);

                // Kill 메서드 있으면 나중에 강제 킬용으로 호출
                try
                {
                    _healthKillMethod = healthType.GetMethod(
                        "Kill",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );
                }
                catch
                {
                    _healthKillMethod = null;
                }

                _healthIsDeadProperty = healthType.GetProperty(
                    "IsDead",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );

                Debug.Log("[SecondChance] Health 리플렉션 캐시 완료 - Kill=" +
                          (_healthKillMethod != null) + ", IsDead=" +
                          (_healthIsDeadProperty != null));
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] Health 리플렉션 캐시 실패: " + ex);
            }
        }

        private void CreateGrayOverlayTexture()
        {
            try
            {
                _grayOverlayTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _grayOverlayTex.SetPixel(0, 0, Color.white);
                _grayOverlayTex.Apply();
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] 회색 오버레이 텍스처 생성 실패: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // Harmony 엔트리
        // ─────────────────────────────────────────────
        public static void OnHealthSetCurrentHealthPrefix(Health health, ref float value)
        {
            if (_instance == null || health == null)
                return;

            _instance.HandleSetCurrentHealth(health, ref value);
        }

        // CurrentHealth setter 직전 처리
        private void HandleSetCurrentHealth(Health health, ref float value)
        {
            if (!IsPlayerHealth(health))
                return;

            // 아직 세컨드 찬스를 안 쓴 상태에서, HP를 올려주는 값이면 최대 체력 후보로 갱신
            if (!_secondChanceUsed && !_isDowned && value > _playerMaxObservedHealth)
            {
                _playerMaxObservedHealth = value;
            }

            // ─────────────────────────────────────
            // ★ 첫 lethal damage: HP를 0 이하로 쓰려 할 때
            // ─────────────────────────────────────
            if (!_secondChanceUsed && !_isDowned && value <= 0.001f)
            {
                float baseMax = _playerMaxObservedHealth;
                if (baseMax < 1f)
                {
                    float cur = health.CurrentHealth;
                    if (cur > baseMax) baseMax = cur;
                    if (baseMax < 1f) baseMax = 100f;
                }

                float downHp = baseMax * DownHpPercent;
                if (downHp < MinDownHealth)
                    downHp = MinDownHealth;

                _isDowned = true;
                _secondChanceUsed = true;
                _downTimer = DownDuration;
                _downStartHealth = downHp;

                // 이번에 기록하려던 값을 아예 99% HP로 바꿔버림
                value = downHp;

                TryClearIsDeadFlag(health);
                CachePlayerDeadFlags();
                HideHpUI();

                Debug.Log($"[SecondChance] set_CurrentHealth: 0 이하 감지 → 세컨드 찬스 발동, HP={downHp}, 시간={DownDuration}s");
                return;
            }

            // 다운 상태일 때는 HP가 1 밑으로 절대 내려가지 않도록 보정
            if (_isDowned && value < MinDownHealth)
            {
                Debug.Log($"[SecondChance] set_CurrentHealth: 다운 상태에서 HP {value} → {MinDownHealth}로 보정");
                value = MinDownHealth;
            }
        }

        // ─────────────────────────────────────────────
        // 플레이어 Health 판정 + 새 플레이어 감지 시 초기화
        // ─────────────────────────────────────────────
        private bool IsPlayerHealth(Health health)
        {
            if (health == null)
                return false;

            // 1) 이미 캐시된 플레이어 Health면 OK
            if (_playerHealth == health && _player != null)
                return true;

            // 2) 아직 모르면 카메라 기준 가장 가까운 CharacterMainControl을 플레이어로
            if (!EnsurePlayer())
                return false;

            // 3) 캐시된 플레이어 Health와 같은 인스턴스면 플레이어
            return _playerHealth == health;
        }

        // ─────────────────────────────────────────────
        // 매 프레임 업데이트
        // ─────────────────────────────────────────────
        private void Update()
        {
            if (!EnsurePlayer())
                return;

            if (_playerHealth == null)
                return;

            float hp = _playerHealth.CurrentHealth;

            if (!_isDowned)
            {
                // 다운 상태가 아니면, 관찰된 최대 체력 갱신만
                if (hp > _playerMaxObservedHealth)
                    _playerMaxObservedHealth = hp;

                return;
            }

            // 다운 상태일 때: 혹시라도 HP가 내려가 있으면 다시 1로
            if (_playerHealth.CurrentHealth < MinDownHealth)
                _playerHealth.CurrentHealth = MinDownHealth;

            TryClearIsDeadFlag(_playerHealth);
            TryClearPlayerDeadFlags();

            // ★ 항상 “현실 시간” 10초 기준으로 깎이게 언스케일드 타임 사용
            _downTimer -= Time.unscaledDeltaTime;

            // 다운 시작 HP(99%)보다 더 회복하면 생존
            if (_playerHealth.CurrentHealth > (_downStartHealth + 0.5f))
            {
                ExitDownState(success: true);
                return;
            }

            // ★ 시간 초과 → 강제 사망
            if (_downTimer <= 0f)
            {
                ForceKillPlayer();
                return;
            }
        }

        private void ExitDownState(bool success)
        {
            if (!_isDowned)
                return;

            _isDowned = false;
            RestoreHpUI();

            if (success)
                Debug.Log("[SecondChance] 다운 종료 - 추가 회복 성공 (생존)");
            else
                Debug.Log("[SecondChance] 다운 종료 - 시간 초과 (사망 처리 진행)");
        }

        // 타이머 0 초과 시 강제 사망 처리
        private void ForceKillPlayer()
        {
            if (_playerHealth == null)
                return;

            // 다운 상태 종료 + UI 복구 (이제 더 이상 무적/보정 안 걸림)
            ExitDownState(success: false);

            try
            {
                bool killInvoked = false;

                // Health.Kill() 메서드가 있으면 가능한 한 그걸 호출
                if (_healthKillMethod != null)
                {
                    try
                    {
                        var ps = _healthKillMethod.GetParameters();
                        object[] args = null;

                        if (ps != null && ps.Length > 0)
                        {
                            args = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                Type pt = ps[i].ParameterType;
                                args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                            }
                        }

                        _healthKillMethod.Invoke(_playerHealth, args);
                        killInvoked = true;
                        Debug.Log("[SecondChance] ForceKillPlayer: Health.Kill() 호출");
                    }
                    catch (Exception ex)
                    {
                        Debug.Log("[SecondChance] ForceKillPlayer Kill 호출 실패: " + ex);
                    }
                }

                // Kill 메서드를 못 찾았거나 호출 실패했다면, IsDead=true + HP=0으로 강제
                if (!killInvoked)
                {
                    if (_healthIsDeadProperty != null && _healthIsDeadProperty.CanWrite)
                    {
                        _healthIsDeadProperty.SetValue(_playerHealth, true, null);
                    }

                    _playerHealth.CurrentHealth = 0f;
                    Debug.Log("[SecondChance] ForceKillPlayer: Kill 없음 → HP=0 / IsDead=true 설정");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] ForceKillPlayer 예외: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // IsDead 플래그 / death bool 리셋
        // ─────────────────────────────────────────────
        private void TryClearIsDeadFlag(Health health)
        {
            if (health == null || _healthIsDeadProperty == null)
                return;

            try
            {
                if (_healthIsDeadProperty.CanRead)
                {
                    object v = _healthIsDeadProperty.GetValue(health, null);
                    if (v is bool && (bool)v == true)
                    {
                        if (_healthIsDeadProperty.CanWrite)
                            _healthIsDeadProperty.SetValue(health, false, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] IsDead 리셋 실패: " + ex);
            }
        }

        private void CachePlayerDeadFlags()
        {
            _playerDeadFlagFields = null;

            if (_player == null)
                return;

            try
            {
                List<FieldInfo> list = new List<FieldInfo>();
                FieldInfo[] fields = _player.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );

                foreach (FieldInfo f in fields)
                {
                    if (f.FieldType != typeof(bool))
                        continue;

                    string name = f.Name.ToLowerInvariant();
                    if (name.Contains("isdead") || name == "dead" || name.Contains("deadflag"))
                        list.Add(f);
                }

                if (list.Count > 0)
                {
                    _playerDeadFlagFields = list.ToArray();
                    Debug.Log("[SecondChance] death bool 필드 캐시: " + _playerDeadFlagFields.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] death 필드 캐시 실패: " + ex);
            }
        }

        private void TryClearPlayerDeadFlags()
        {
            if (_player == null || _playerDeadFlagFields == null)
                return;

            try
            {
                for (int i = 0; i < _playerDeadFlagFields.Length; i++)
                {
                    FieldInfo f = _playerDeadFlagFields[i];
                    if (f == null) continue;

                    object v = f.GetValue(_player);
                    if (v is bool && (bool)v == true)
                        f.SetValue(_player, false);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] death bool 리셋 실패: " + ex);
            }
        }

        // ─────────────────────────────────────────────
        // HP UI 숨기기 / 복구
        // ─────────────────────────────────────────────
        private void HideHpUI()
        {
            if (_hpUiHidden)
                return;

            _hpUiHidden = true;
            _hpUiObjects.Clear();

            try
            {
                Transform[] allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (Transform t in allTransforms)
                {
                    if (t == null) continue;

                    GameObject go = t.gameObject;
                    if (go == null) continue;

                    string name = go.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    string lower = name.ToLowerInvariant();

                    if (lower.Contains("hp") || lower.Contains("health") || lower.Contains("체력"))
                    {
                        if (go.activeSelf)
                        {
                            _hpUiObjects.Add(go);
                            go.SetActive(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] HideHpUI 예외: " + ex);
            }
        }

        private void RestoreHpUI()
        {
            if (!_hpUiHidden)
                return;

            try
            {
                for (int i = 0; i < _hpUiObjects.Count; i++)
                {
                    GameObject go = _hpUiObjects[i];
                    if (go != null)
                        go.SetActive(true);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] RestoreHpUI 예외: " + ex);
            }

            _hpUiObjects.Clear();
            _hpUiHidden = false;
        }

        // ─────────────────────────────────────────────
        // 플레이어 찾기 (카메라 기준 가장 가까운 CharacterMainControl)
        // ─────────────────────────────────────────────
        private bool EnsurePlayer()
        {
            if (_player != null && _playerHealth != null)
                return true;

            _player = null;
            _playerHealth = null;
            _playerDeadFlagFields = null;

            try
            {
                CharacterMainControl[] all = GameObject.FindObjectsOfType<CharacterMainControl>();
                if (all == null || all.Length == 0)
                    return false;

                Camera cam = Camera.main;
                float bestDist = float.MaxValue;
                CharacterMainControl best = null;

                foreach (CharacterMainControl c in all)
                {
                    if (c == null) continue;

                    float dist = 0f;
                    if (cam != null)
                        dist = Vector3.Distance(cam.transform.position, c.transform.position);

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = c;
                    }
                }

                if (best != null)
                {
                    _player = best;
                    _playerHealth = _player.GetComponent<Health>();

                    if (_playerHealth != null)
                    {
                        int newId = _player.GetInstanceID();
                        if (newId != _playerInstanceId)
                        {
                            _playerInstanceId = newId;
                            _secondChanceUsed = false;
                            _isDowned = false;
                            _playerMaxObservedHealth = _playerHealth.CurrentHealth;
                            RestoreHpUI();

                            Debug.Log("[SecondChance] EnsurePlayer: 새 플레이어 감지 - 상태 초기화");
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[SecondChance] EnsurePlayer 예외: " + ex);
            }

            return false;
        }

        // ─────────────────────────────────────────────
        // 로컬라이즈된 안내 문구 + HUD
        // ─────────────────────────────────────────────
        private string GetDownMessage(float remain)
        {
            SystemLanguage lang = Application.systemLanguage;

            if (lang == SystemLanguage.Japanese)
            {
                return string.Format(
                    "仮回復状態！ （この人生で1回限定、HPが最大値の99％に固定されています） {0:0.0}秒以内に回復アイテムでさらに回復しないと死亡します。",
                    remain
                );
            }
            else if (lang == SystemLanguage.English)
            {
                return string.Format(
                    "Second chance! (Once per life, HP fixed to 99% of max) Use a healing item to recover more within {0:0.0} seconds to survive.",
                    remain
                );
            }
            else
            {
                return string.Format(
                    "임시 회복 상태! (이번 생 1회 한정, 체력이 최대의 99%로 고정되었습니다) {0:0.0}초 안에 회복 아이템으로 추가 회복을 해야 살아남습니다.",
                    remain
                );
            }
        }

        private void OnGUI()
        {
            if (!_isDowned)
                return;

            if (_grayOverlayTex != null)
            {
                Color oldColor = GUI.color;
                GUI.color = new Color(0.9f, 0.9f, 0.9f, 0.25f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _grayOverlayTex);
                GUI.color = oldColor;
            }

            if (_downLabelStyle == null)
            {
                _downLabelStyle = new GUIStyle(GUI.skin.label);
                _downLabelStyle.fontSize = 20;
                _downLabelStyle.normal.textColor = Color.red;
                _downLabelStyle.wordWrap = true;
            }

            float remain = Mathf.Max(0f, _downTimer);
            string msg = GetDownMessage(remain);

            float margin = 20f;
            float w = Screen.width - margin * 2f;
            float h = 100f;
            float x = margin;
            float y = 150f;

            GUI.Label(new Rect(x, y, w, h), msg, _downLabelStyle);
        }
    }
}
