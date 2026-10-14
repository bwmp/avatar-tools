using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Core;

namespace AvatarTools.Editor
{
    public class AccountSwitcher : EditorWindow
    {
        // UserSettings is per project and excluded from version control by Unity's default ignore rules.
        static readonly string StorePath = Path.Combine("UserSettings", "AvatarTools", "VRChatAccounts.dat");

        [Serializable]
        class Account
        {
            public string userId;
            public string displayName;
            public string humanName;
            public string providerUserId;
            public string provider;
            public string authToken;
            public string twoFactorAuthToken;
        }

        [Serializable]
        class Store
        {
            public List<Account> accounts = new List<Account>();
        }

        Store store;
        string loadError;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Workflow/VRChat Account Switcher")]
        static void Open()
        {
            GetWindow<AccountSwitcher>("Accounts");
        }

        void OnEnable() => Load();
        void OnInspectorUpdate() => Repaint();

        void Load()
        {
            loadError = null;
            store = new Store();
            if (!File.Exists(StorePath)) return;
            try
            {
                var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(StorePath)));
                store = JsonUtility.FromJson<Store>(json) ?? new Store();
            }
            catch (Exception e)
            {
                // Saving over a file that could not be read would lose every stored account.
                loadError = $"Could not read {StorePath}: {e.Message}";
                Debug.LogException(e);
            }
        }

        void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
            File.WriteAllBytes(StorePath, Dpapi.Protect(Encoding.UTF8.GetBytes(JsonUtility.ToJson(store))));
        }

        void OnGUI()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                EditorGUILayout.HelpBox("The account switcher stores sessions with Windows data protection and only works on Windows.", MessageType.Info);
                return;
            }
            if (loadError != null)
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                if (GUILayout.Button("Retry")) Load();
                return;
            }

            var current = APIUser.CurrentUser;
            var currentSaved = current != null && store.accounts.Any(a => a.userId == current.id);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(current != null ? "Signed in as " + current.displayName : "Not signed in", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(current == null || string.IsNullOrEmpty(ApiCredentials.GetAuthToken())))
                {
                    if (GUILayout.Button(currentSaved ? "Update Saved" : "Save Account", GUILayout.Width(110))) SaveCurrent();
                }
            }
            EditorGUILayout.HelpBox(
                "Do not use Logout in the VRChat SDK panel for a saved account. It ends that session on VRChat's servers, " +
                "so the saved copy stops working and has to be saved again after signing back in.", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Saved Accounts", EditorStyles.boldLabel);
            if (store.accounts.Count == 0)
                EditorGUILayout.LabelField("None yet. Sign in through the VRChat SDK panel, then save the account here.", EditorStyles.wordWrappedMiniLabel);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var account in store.accounts.ToList())
            {
                var isCurrent = current != null && current.id == account.userId;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(account.displayName + (isCurrent ? "  (current)" : ""));
                    using (new EditorGUI.DisabledScope(isCurrent))
                    {
                        if (GUILayout.Button("Switch", EditorStyles.miniButton, GUILayout.Width(60))) SwitchTo(account);
                    }
                    if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60)) &&
                        EditorUtility.DisplayDialog("Remove Account",
                            $"Forget the saved session for {account.displayName}? You stay signed in on VRChat; it is only removed from this list.", "Remove", "Cancel"))
                    {
                        store.accounts.Remove(account);
                        Save();
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            if (GUILayout.Button("Sign In to Another Account") && ConfirmLeavingCurrent())
            {
                ClearLocalSession();
                ReopenControlPanel();
            }
        }

        void SaveCurrent()
        {
            var user = APIUser.CurrentUser;
            store.accounts.RemoveAll(a => a.userId == user.id);
            store.accounts.Add(new Account
            {
                userId = user.id,
                displayName = user.displayName,
                humanName = ApiCredentials.GetHumanName(),
                providerUserId = ApiCredentials.GetAuthTokenProviderUserId(),
                provider = ApiCredentials.GetAuthTokenProvider(),
                authToken = ApiCredentials.GetAuthToken(),
                twoFactorAuthToken = ApiCredentials.GetTwoFactorAuthToken() ?? ""
            });
            store.accounts.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        void SwitchTo(Account account)
        {
            if (!ConfirmLeavingCurrent()) return;
            ClearLocalSession();
            if (string.IsNullOrEmpty(account.twoFactorAuthToken))
                ApiCredentials.Set(account.humanName, account.providerUserId, account.provider, account.authToken);
            else
                ApiCredentials.Set(account.humanName, account.providerUserId, account.provider, account.authToken, account.twoFactorAuthToken);
            ReopenControlPanel();
        }

        bool ConfirmLeavingCurrent()
        {
            var current = APIUser.CurrentUser;
            if (current == null || store.accounts.Any(a => a.userId == current.id)) return true;
            var choice = EditorUtility.DisplayDialogComplex("Unsaved Account",
                $"{current.displayName} is not saved. Save it so you can switch back without signing in again?",
                "Save", "Cancel", "Don't Save");
            if (choice == 1) return false;
            if (choice == 0) SaveCurrent();
            return true;
        }

        // APIUser.Logout would revoke the session on VRChat's servers, which kills the saved copy, so only local state is cleared.
        static void ClearLocalSession()
        {
            ApiCredentials.Clear();
            VRC.Tools.ClearCookies();
            var currentUser = typeof(APIUser).GetProperty(nameof(APIUser.CurrentUser));
            var setter = currentUser?.GetSetMethod(true)
                ?? throw new MissingMemberException("APIUser.CurrentUser has no setter in this VRChat SDK version.");
            setter.Invoke(null, new object[] { null });
        }

        // The panel only loads stored credentials when it opens, so it is reopened to pick up the new session.
        static void ReopenControlPanel()
        {
            if (VRCSdkControlPanel.window != null) VRCSdkControlPanel.window.Close();
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
        }

        static class Dpapi
        {
            const int UiForbidden = 0x1;

            [StructLayout(LayoutKind.Sequential)]
            struct DataBlob
            {
                public int cbData;
                public IntPtr pbData;
            }

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy, IntPtr reserved,
                IntPtr prompt, int flags, ref DataBlob dataOut);

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
                IntPtr prompt, int flags, ref DataBlob dataOut);

            [DllImport("kernel32.dll")]
            static extern IntPtr LocalFree(IntPtr mem);

            public static byte[] Protect(byte[] data) => Transform(data, true);
            public static byte[] Unprotect(byte[] data) => Transform(data, false);

            static byte[] Transform(byte[] data, bool protect)
            {
                var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
                var output = new DataBlob();
                try
                {
                    Marshal.Copy(data, 0, input.pbData, data.Length);
                    var ok = protect
                        ? CryptProtectData(ref input, "Avatar Tools VRChat accounts", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                        : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                    if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var result = new byte[output.cbData];
                    Marshal.Copy(output.pbData, result, 0, output.cbData);
                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(input.pbData);
                    if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                }
            }
        }
    }
}
