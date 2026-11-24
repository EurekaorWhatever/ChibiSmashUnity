using System;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Authentication.PlayerAccounts;
using Unity.Services.Core;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// The player's account: SIGN IN WITH GOOGLE through Unity Player Accounts (the browser opens Unity's sign-in page,
    /// where the player picks "Continue with Google"; a browser already signed in to Google needs one click). The Unity
    /// Authentication player is then bound to that account, so the same account brings back the same player id and
    /// player name on every launch and on every computer. Unity caches the session: the next launch signs the same
    /// account in automatically (no browser). SIGN OUT clears it; a different Google account is a different player.
    /// The game keeps one local profile per account (PlayerIdentity.SwitchProfile). Setup once in the Unity Dashboard:
    /// Player Accounts on, Google enabled as a sign-in option. Main thread only.
    /// </summary>
    /// <example><code>
    /// account.AccountChanged += (id, serverName) => PlayerIdentity.SwitchProfile(save, id, serverName);
    /// account.SignInWithGoogle();
    /// </code></example>
    public sealed class ChibiAccount : MonoBehaviour
    {
        /// <summary>Signed in with an account (not a guest).</summary>
        public bool SignedIn { get; private set; }
        /// <summary>Sign-in / sign-out in progress.</summary>
        public bool Working { get; private set; }
        public string Status { get; private set; } = "";
        public bool StatusIsError { get; private set; }

        /// <summary>Age gate: accounts are for players 13 and older (set by the game).</summary>
        public Func<bool> OnlineAllowed = () => true;

        /// <summary>
        /// Raised on the main thread when the account changes: (account id, the account's server-side player name such as
        /// "Kira#4821", or "" when it has none yet). Id "" = signed out (guest).
        /// </summary>
        public event Action<string, string> AccountChanged;

        bool _subscribed;

        /// <summary>Open the browser sign-in (Google). The result arrives through <see cref="AccountChanged"/>.</summary>
        public async void SignInWithGoogle()
        {
            if (Working) return;
            if (OnlineAllowed != null && !OnlineAllowed()) { SetStatus(NetworkRoomManager.NotAllowedMessage, true); return; }
            if (Application.internetReachability == NetworkReachability.NotReachable) { SetStatus(NetworkRoomManager.NoInternetMessage, true); return; }
            if (string.IsNullOrEmpty(Application.cloudProjectId)) { SetStatus(NetworkRoomManager.NotLinkedMessage, true); return; }
            Working = true;
            SetStatus(Loc.T("account.finish_browser"), false);
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized) await UnityServices.InitializeAsync();
                Subscribe();
                if (PlayerAccountService.Instance.IsSignedIn) { await CompleteSignIn(); return; }
                await PlayerAccountService.Instance.StartSignInAsync();   // browser; SignedIn event follows
            }
            catch (Exception e)
            {
                Working = false;
                SetStatus(Loc.T("net.sign_in_failed", NetworkRoomManager.Explain(e)), true);
            }
        }

        /// <summary>
        /// On launch: if this computer has a remembered account session, sign it in silently (no browser) and report it.
        /// Offline or not linked: nothing happens and the cached local profile is used.
        /// </summary>
        public async void RestoreOnLaunch(string savedAccountId)
        {
            if (string.IsNullOrEmpty(savedAccountId) || Working) return;
            if (OnlineAllowed != null && !OnlineAllowed()) return;
            if (Application.internetReachability == NetworkReachability.NotReachable || string.IsNullOrEmpty(Application.cloudProjectId)) return;
            Working = true;
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized) await UnityServices.InitializeAsync();
                ChibiDevFlags.ApplyAuthProfile();   // dev only (-authProfile); inert in release
                var auth = AuthenticationService.Instance;
                if (!auth.IsSignedIn && auth.SessionTokenExists) await auth.SignInAnonymouslyAsync();   // resumes the cached account session
                if (auth.IsSignedIn && auth.PlayerId == savedAccountId)
                {
                    SignedIn = true;
                    string name = await auth.GetPlayerNameAsync(autoGenerate: false);
                    AccountChanged?.Invoke(auth.PlayerId, name ?? "");
                }
            }
            catch (Exception e) { Debug.LogWarning("ChibiClash account restore: " + NetworkRoomManager.Explain(e)); }
            finally { Working = false; }
        }

        /// <summary>Signed in: make this the account's public name now (validated), so every computer shows it.</summary>
        public async void PushName(string name)
        {
            if (!SignedIn || ChibiSocialRules.ValidateName(name) != null) return;
            try { await AuthenticationService.Instance.UpdatePlayerNameAsync(name); }
            catch (Exception e) { Debug.LogWarning("ChibiClash name push: " + NetworkRoomManager.Explain(e)); }
        }

        /// <summary>Sign out (to switch Google account): this computer forgets the session; the game becomes a guest.</summary>
        public void SignOut()
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized)
                {
                    if (AuthenticationService.Instance.IsSignedIn) AuthenticationService.Instance.SignOut(clearCredentials: true);
                    else AuthenticationService.Instance.ClearSessionToken();
                    if (PlayerAccountService.Instance.IsSignedIn) PlayerAccountService.Instance.SignOut();
                }
            }
            catch (Exception e) { Debug.LogWarning("ChibiClash sign-out: " + NetworkRoomManager.Explain(e)); }
            SignedIn = false;
            SetStatus(Loc.T("account.signed_out_again"), false);
            AccountChanged?.Invoke("", "");
        }

        void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            PlayerAccountService.Instance.SignedIn += OnPlayerAccountSignedIn;
            PlayerAccountService.Instance.SignInFailed += OnPlayerAccountFailed;
        }

        void OnDestroy()
        {
            if (!_subscribed) return;
            try
            {
                PlayerAccountService.Instance.SignedIn -= OnPlayerAccountSignedIn;
                PlayerAccountService.Instance.SignInFailed -= OnPlayerAccountFailed;
            }
            catch (Exception) { /* services shut down */ }
        }

        void OnPlayerAccountSignedIn() => _ = CompleteSignIn();

        void OnPlayerAccountFailed(RequestFailedException e)
        {
            Working = false;
            SetStatus(Loc.T("net.sign_in_failed", NetworkRoomManager.Explain(e)), true);
        }

        /// <summary>Bind the game's player to the signed-in account (a guest session is replaced, never merged).</summary>
        async Task CompleteSignIn()
        {
            try
            {
                var auth = AuthenticationService.Instance;
                string token = PlayerAccountService.Instance.AccessToken;
                if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("no access token");
                if (auth.IsSignedIn) auth.SignOut(clearCredentials: true);
                await auth.SignInWithUnityAsync(token);
                SignedIn = true;
                string name = await auth.GetPlayerNameAsync(autoGenerate: false);
                SetStatus(Loc.T("account.signed_in_kept"), false);
                AccountChanged?.Invoke(auth.PlayerId, name ?? "");
            }
            catch (Exception e) { SetStatus(Loc.T("net.sign_in_failed", NetworkRoomManager.Explain(e)), true); }
            finally { Working = false; }
        }

        void SetStatus(string s, bool error)
        {
            Status = s ?? "";
            StatusIsError = error;
        }
    }
}
