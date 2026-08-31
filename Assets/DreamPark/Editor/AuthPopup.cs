#if UNITY_EDITOR
using System;
using DreamPark.API;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark
{
    // Modal popup window for logging into DreamPark. Used by editor panels
    // that gate on AuthAPI.isLoggedIn — the panel shows a "Log in" button that
    // opens this popup, which closes itself on successful login. Subscribers
    // to AuthAPI.LoginStateChanged repaint to reveal panel contents.
    //
    // PASSWORDLESS (July 2026). Two panes, one flow: enter your email, then enter
    // the 6-digit code we email you. There is no password field because DreamPark
    // accounts no longer have passwords — anyone who signed up after the switch has
    // no credential this window could have asked for, which is why the old form
    // locked new creators out of the SDK entirely. There is also no separate signup
    // path: POST /auth/otp/verify get-or-creates the account, so "sign up" and
    // "log in" are the same three keystrokes.
    public class AuthPopup : EditorWindow
    {
        // How long "Resend code" stays disabled after a send. The server allows only
        // 3 sends per email per 10 minutes (DreamPark-Web/lib/authOtp.js), so this is
        // a courtesy that stops an impatient user burning all three in the ten seconds
        // before the first email lands and then sitting locked out with no recourse.
        private const double ResendCooldownSeconds = 30;

        private string email = "";
        private string code = "";
        // false = pane 1 (email), true = pane 2 (code).
        private bool codeSent = false;
        private bool isSubmitting = false;
        private string errorMessage = null;
        // AGE_UNVERIFIED copy — deliberately NOT an error, because it names a step the
        // user can actually take. Held in its own field so it and a genuine error can't
        // overwrite each other.
        private string ageGateMessage = null;
        // AGE_BLOCKED_UNDER_13 copy, kept SEPARATE from ageGateMessage because the two age
        // outcomes are opposites: one is "we don't know your age yet", the other is a
        // permanent refusal that the web answers with a parent-invite flow. One field would
        // mean one piece of copy and one set of buttons, which is how an under-13 ends up
        // being told to go finish setting up the account they are not allowed to have.
        private string ageBlockedMessage = null;
        private bool focusedOnce = false;
        private bool focusedCodeOnce = false;
        // EditorApplication.timeSinceStartup value after which "Resend code" re-enables.
        private double resendAvailableAt = 0;
        // The last code value we auto-submitted, so reaching six digits fires exactly one
        // verify. Without it, every repaint after a rejected code would resubmit the same
        // code and burn all five of the server's attempts in a fraction of a second.
        private string autoSubmittedCode = null;
        // Bumped whenever an in-flight request is superseded or abandoned. Each callback
        // captures the value at send time and drops itself if it no longer matches — see
        // IsStale. Requests are started with EditorCoroutineUtility.StartCoroutineOwnerless
        // (DreamParkAPI), so they outlive both this window and whatever pane the user was
        // on when they fired.
        private int flowGeneration = 0;
        // Set in OnDestroy. Same purpose as the fake-null check in IsStale, but it does not
        // depend on when Unity flips the native pointer.
        private bool destroyed = false;

        // Fixed size (min == max): two fixed panes, and a resizable window just invites a
        // 120px-tall one that clips its own copy. 210 rather than the original 180 because
        // the AGE_UNVERIFIED recovery is three lines of HelpBox plus a button — at 180 the
        // last line and the button fell off the bottom, which would leave the recovery as
        // unreachable as the bug it exists to fix.
        private const float WindowWidth  = 360f;
        private const float WindowHeight = 210f;

        public static void Show()
        {
            // If a popup is already open, refocus it instead of stacking another.
            var existing = Resources.FindObjectsOfTypeAll<AuthPopup>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].Focus();
                return;
            }

            var win = CreateInstance<AuthPopup>();
            win.titleContent = new GUIContent("Log in to DreamPark");
            win.minSize = new Vector2(WindowWidth, WindowHeight);
            win.maxSize = new Vector2(WindowWidth, WindowHeight);
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - WindowWidth) / 2f,
                main.y + (main.height - WindowHeight) / 2f,
                WindowWidth, WindowHeight);
            win.ShowUtility();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnDestroy()
        {
            // The window is gone; the requests it started are not. Mark the instance dead
            // so their callbacks drop themselves instead of calling Repaint()/Close() on a
            // destroyed EditorWindow.
            destroyed = true;
        }

        // IMGUI has no timers — the resend countdown only appears to tick if something
        // asks for a repaint. Drive it off EditorApplication.update (the same idiom
        // ContentUploadFlowPopup uses for its progress bar) and only while a countdown is
        // actually running, so an idle popup isn't repainting every frame forever.
        private void OnEditorUpdate()
        {
            if (!codeSent || resendAvailableAt <= 0) return;

            if (EditorApplication.timeSinceStartup < resendAvailableAt)
            {
                Repaint();
                return;
            }

            // ONE more frame after the deadline, which is the frame that matters. Repainting
            // only while the deadline is in the future means the last frame ever drawn shows
            // CeilToInt of a fraction of a second — "Resend (1s)" — with the button still
            // disabled. The countdown appears to stick at 1, and the user's first click only
            // flips the label to its enabled state, so they have to click again to actually
            // resend. Zeroing the deadline makes this a one-shot rather than a repaint every
            // frame for the life of the window.
            resendAvailableAt = 0;
            Repaint();
        }

        // True when a callback must NOT touch this window's state, for either of the two
        // reasons a callback can arrive too late:
        //   * the window is gone — the user closed it, or a successful verify already
        //     called Close(). Repaint()/Close() on a destroyed EditorWindow throws.
        //   * the flow it belonged to was abandoned or superseded (see flowGeneration),
        //     so its result would land on a pane the user has already moved off.
        // `this == null` is Unity's fake-null: true once the native object is destroyed.
        private bool IsStale(int generation)
        {
            return destroyed || this == null || generation != flowGeneration;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Sign in to DreamPark", EditorStyles.boldLabel);
            GUILayout.Space(4);

            if (!codeSent)
            {
                DrawEmailPane();
            }
            else
            {
                DrawCodePane();
            }
        }

        // Pane 1 — the address, and nothing else.
        private void DrawEmailPane()
        {
            GUI.enabled = !isSubmitting;

            GUI.SetNextControlName("AuthPopup_Email");
            email = EditorGUILayout.TextField("Email", email);

            if (!focusedOnce)
            {
                EditorGUI.FocusTextInControl("AuthPopup_Email");
                focusedOnce = true;
            }

            EditorGUILayout.LabelField("We'll email you a 6-digit sign-in code.", EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(errorMessage))
            {
                EditorGUILayout.HelpBox(errorMessage, MessageType.Error);
            }

            GUI.enabled = true;
            GUILayout.FlexibleSpace();

            DrawDeveloperProgramLink();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }

            // A plausible-looking address is ALL we check. The server validates properly
            // (authOtp.isPlausibleEmail) and answers 200 either way, and we must not add
            // any check of our own that behaves differently for a known address than for
            // an unknown one — that difference is precisely the account-enumeration
            // oracle /auth/otp/request is built to never be.
            bool canSubmit = !string.IsNullOrEmpty(email) && email.Contains("@");
            GUI.enabled = !isSubmitting && canSubmit;
            bool clicked = GUILayout.Button(isSubmitting ? "Sending..." : "Send me a code");
            bool enterPressed = Event.current.type == EventType.KeyDown
                                 && Event.current.keyCode == KeyCode.Return
                                 && GUI.enabled;
            if (clicked || enterPressed)
            {
                SendCode();
                if (enterPressed) Event.current.Use();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // Pane 2 — the code. Note what this copy does NOT say: not "we sent a code to
        // your account", not "no account found". /auth/otp/request answers 200 for every
        // plausible address — unknown, throttled and suppressed addresses are all
        // indistinguishable by design — and this pane is the last place that guarantee
        // could leak back out.
        private void DrawCodePane()
        {
            EditorGUILayout.LabelField("Check your email for a 6-digit code.", EditorStyles.miniLabel);

            // AGE_BLOCKED_UNDER_13 is a permanent refusal, so every affordance that reads
            // as "try this address again" goes dead: the code field, Resend, auto-submit
            // and the Enter re-submit. Leaving any of them live would invite the user to
            // spend the server's five attempts disproving a "no" that was never about the
            // code. Cancel and "Use a different email" stay — those are the only two moves
            // that can actually change the answer.
            bool blocked = !string.IsNullOrEmpty(ageBlockedMessage);

            GUI.enabled = !isSubmitting && !blocked;

            GUI.SetNextControlName("AuthPopup_Code");
            // A plain TextField, never PasswordField: this is a short-lived one-time code
            // being copied out of an email, so masking it hides typos and protects
            // nothing. Filtered to at most six digits so the field cannot hold anything
            // /auth/otp/verify would reject outright (it requires /^\d{6}$/).
            code = DigitsOnly(EditorGUILayout.TextField("6-digit code", code), 6);

            if (!focusedCodeOnce)
            {
                EditorGUI.FocusTextInControl("AuthPopup_Code");
                focusedCodeOnce = true;
            }

            // Messages are drawn at full contrast whatever the controls above are doing —
            // a refusal the user has to squint at is worse than no refusal at all.
            GUI.enabled = true;

            GUILayout.Space(4);

            if (blocked)
            {
                // The server's own wording ("Account holders must be 13 or older."), with
                // NO next step offered. The web answers this code by opening a parent-invite
                // flow; the SDK has no equivalent surface, and inventing a next step here
                // would be exactly the kind of guess that made the age branch dead code in
                // the first place.
                EditorGUILayout.HelpBox(ageBlockedMessage, MessageType.Error);
            }
            else if (!string.IsNullOrEmpty(ageGateMessage))
            {
                // Recoverable, not fatal — the account just has no birthday on file yet.
                // The only action offered is the web page; the way BACK into the SDK is the
                // Resend button in the row below, which is what the copy points at.
                EditorGUILayout.HelpBox(ageGateMessage, MessageType.Warning);
                GUILayout.BeginHorizontal();
                GUI.enabled = !isSubmitting;
                if (GUILayout.Button("Open dreampark.app", GUILayout.Width(150)))
                {
                    Application.OpenURL("https://dreampark.app/login");
                }
                GUI.enabled = true;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else if (!string.IsNullOrEmpty(errorMessage))
            {
                EditorGUILayout.HelpBox(errorMessage, MessageType.Error);
            }

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            // Deliberately LIVE while a request is in flight. The alternative — disabling
            // the link until the response lands — trades one trap for another: a verify can
            // sit for the full 30s request timeout (DreamParkAPI.RequestTimeoutSeconds), and
            // a link that ignores half a minute of clicking is no better than the pane it
            // used to strand the user on. It is safe because BackToEmailPane() bumps
            // flowGeneration, so the abandoned request's callback drops itself: it cannot
            // splash an error about a discarded code onto pane 1, and it cannot Close() this
            // window on a user who just said they wanted a different address.
            if (GUILayout.Button("Use a different email", EditorStyles.linkLabel, GUILayout.Width(140)))
            {
                BackToEmailPane();
            }
            GUILayout.FlexibleSpace();

            double secondsLeft = resendAvailableAt - EditorApplication.timeSinceStartup;
            GUI.enabled = !isSubmitting && !blocked && secondsLeft <= 0;
            string resendLabel = secondsLeft > 0
                ? string.Format("Resend ({0}s)", Mathf.CeilToInt((float)secondsLeft))
                : "Resend code";
            if (GUILayout.Button(resendLabel, GUILayout.Width(90)))
            {
                SendCode();
            }
            GUI.enabled = true;

            if (GUILayout.Button("Cancel", GUILayout.Width(60)))
            {
                Close();
            }
            GUILayout.EndHorizontal();

            // Submit checks run LAST, after every control for this pass has been laid
            // out. VerifyCode() clears errorMessage/ageGateMessage synchronously, so
            // running it earlier would drop a HelpBox partway through a pass whose
            // layout was cached with that HelpBox in it — harmless, but it misaligns
            // hit-testing for that one event. Nothing is drawn after this point.
            //
            // Auto-submit the moment the sixth digit lands: there is nothing else on
            // this pane to fill in, so a button press afterwards is pure friction.
            // autoSubmittedCode is set BEFORE the call so the repaints that follow see
            // this code as already tried — otherwise a rejected code would resubmit on
            // every frame and burn all five of the server's attempts instantly.
            if (!blocked && !isSubmitting && code.Length == 6 && code != autoSubmittedCode)
            {
                autoSubmittedCode = code;
                VerifyCode();
            }
            // Enter re-submits the SAME code, which auto-submit deliberately won't — the
            // manual retry for a failure that had nothing to do with the code itself (a
            // dropped connection, a 500), where retyping six digits would be absurd. What
            // it is NOT is the age-gate recovery: that needs a FRESH code, because finishing
            // on the web replaces the one in this field. See VerifyCode.
            else if (!blocked && !isSubmitting && code.Length == 6
                     && Event.current.type == EventType.KeyDown
                     && Event.current.keyCode == KeyCode.Return)
            {
                VerifyCode();
                Event.current.Use();
            }
        }

        // The Developer Program page, kept for the two things that survived the move to
        // passwordless. First, ?developer=true&source=sdk&sdk_version=…&unity_version=…
        // is how the web infers that a creator arrived from the SDK — origin attribution
        // and the referral-source prefill both read those tags, and dropping the link
        // would silently zero that signal. Second, the page is where the SDK Use
        // Agreement is presented. What it is NOT any more is a signup path — the account
        // is created by /auth/otp/verify — so it reads as a link rather than as the other
        // half of a login/signup fork.
        private void DrawDeveloperProgramLink()
        {
            if (GUILayout.Button("Developer Program & SDK terms", EditorStyles.linkLabel))
            {
                string sdkVersion = SDKVersion.Current ?? "unknown";
                string unityVersion = Application.unityVersion ?? "unknown";
                string url = string.Format(
                    "https://dreampark.app/signup?developer=true&source=sdk&sdk_version={0}&unity_version={1}",
                    UnityWebRequest.EscapeURL(sdkVersion),
                    UnityWebRequest.EscapeURL(unityVersion));
                Application.OpenURL(url);
            }
        }

        private void SendCode()
        {
            isSubmitting = true;
            errorMessage = null;
            ageGateMessage = null;
            ageBlockedMessage = null;
            // Claim this generation BEFORE the request goes out; anything already in flight
            // is superseded from here on and its callback will drop itself.
            int generation = ++flowGeneration;
            Repaint();

            AuthAPI.RequestLoginCode(email, (success, response) =>
            {
                if (IsStale(generation)) return;
                isSubmitting = false;
                if (success)
                {
                    // Advance on ANY 200 — which is every plausible address, whether or
                    // not it has an account and whether or not a send actually happened
                    // (throttled and suppressed addresses both answer 200 deliberately).
                    // Branching on anything finer here would rebuild the enumeration
                    // oracle the endpoint exists to avoid.
                    codeSent = true;
                    code = "";
                    autoSubmittedCode = null;
                    focusedCodeOnce = false;
                    resendAvailableAt = EditorApplication.timeSinceStartup + ResendCooldownSeconds;
                }
                else
                {
                    // Only a 400 (malformed address) or a transport failure reaches here.
                    errorMessage = ExtractError(response)
                                   ?? "Could not send a code. Check your connection and try again.";
                }
                Repaint();
            });
        }

        private void VerifyCode()
        {
            isSubmitting = true;
            errorMessage = null;
            ageGateMessage = null;
            ageBlockedMessage = null;
            int generation = ++flowGeneration;
            Repaint();

            AuthAPI.VerifyLoginCode(email, code, (success, response) =>
            {
                if (IsStale(generation)) return;
                isSubmitting = false;
                if (success)
                {
                    // AuthAPI has already stored the session and fired LoginStateChanged;
                    // closing lets the subscribed panels repaint over this window.
                    Close();
                    return;
                }

                // 403 + code — the age gate. The two codes are OPPOSITES and must not share
                // a branch, so they are matched as EXACT strings: they are what
                // lib/ageGate.js assertMayCreateAccount() sets on the error it returns
                // ('AGE_BLOCKED_UNDER_13' / 'AGE_UNVERIFIED'). The "AGE_GATE_*" prefix this
                // used to test for exists only in a prose comment in routes/auth.routes.js;
                // matching on it matched nothing the server has ever sent, which left every
                // brand-new creator staring at a bare "Age has not been verified." and the
                // whole recovery below unreachable.
                string serverCode = null;
                if (response?.json != null && response.json.HasField("code"))
                {
                    serverCode = response.json.GetField("code").stringValue;
                }
                if (response != null && response.statusCode == 403 && serverCode == "AGE_UNVERIFIED")
                {
                    // Recoverable — the account simply has no age band yet. Why the copy
                    // says RESEND and not "enter the same code": the 403 leaves this code
                    // unspent (the server peeks rather than verifies), but finishing on the
                    // web does not. lib/authOtp.js keeps ONE live code per address —
                    // requestOtp's tx.set() overwrites the doc, and a successful verify
                    // deletes it — so the moment the user signs in on the web to answer the
                    // birthday question, the code in this field is dead. Telling them to
                    // reuse it would be telling them to do the one thing guaranteed to fail.
                    ageGateMessage = "Finish setting up your account at dreampark.app "
                                     + "(it asks for your birthday). That replaces this code — "
                                     + "when you're back, press Resend code below.";
                    Repaint();
                    return;
                }
                if (response != null && response.statusCode == 403 && serverCode == "AGE_BLOCKED_UNDER_13")
                {
                    // Permanent refusal, not a step. Show the SERVER's wording rather than
                    // anything of our own — it is the sentence the web and the app also show
                    // — and offer no retry; see DrawCodePane's `blocked`.
                    ageBlockedMessage = ExtractError(response) ?? "Account holders must be 13 or older.";
                    Repaint();
                    return;
                }

                // A 401 names what went wrong in `reason`. Mapping the two recoverable
                // ones to their own copy is the difference between a user who requests a
                // fresh code and one who retypes the dead one until the attempt counter
                // runs out.
                string reason = null;
                if (response?.json != null && response.json.HasField("reason"))
                {
                    reason = response.json.GetField("reason").stringValue;
                }
                if (reason == "expired")
                {
                    errorMessage = "That code has expired. Request a new one.";
                }
                else if (reason == "too-many-attempts")
                {
                    errorMessage = "Too many attempts. Request a new code.";
                }
                else
                {
                    errorMessage = ExtractError(response) ?? "Could not sign you in. Try again.";
                }
                Repaint();
            });
        }

        private void BackToEmailPane()
        {
            // Abandon whatever is in flight. Bumping the generation FIRST means the callback
            // for the request we are walking away from drops itself rather than writing its
            // result into the pane the user just moved to — which is what makes it safe to
            // clear isSubmitting here instead of leaving pane 1 showing a disabled field and
            // a "Sending..." button (a lie: it was a VERIFY in flight) for the rest of a 30s
            // request timeout.
            flowGeneration++;
            isSubmitting = false;

            codeSent = false;
            code = "";
            autoSubmittedCode = null;
            errorMessage = null;
            ageGateMessage = null;
            ageBlockedMessage = null;
            // Re-arm the one-shot focus so the email field is ready to type into again.
            focusedOnce = false;
        }

        // The server's own error message when there is one — those strings are written for
        // end users (see the 401 branch of /auth/otp/verify) and are more specific than
        // anything we could reconstruct from a status code. Falls back to the transport
        // error, then to null so each caller supplies its own default.
        //
        // TWO body shapes are live, and only one of them used to be handled. Route handlers
        // answer { error: "message" } — a STRING. lib/rateLimiter.js answers 429 with
        // { error: { message: "…" } } — an OBJECT, whose .stringValue is null, so the old
        // code fell straight through to response.error and showed the user the raw
        // "HTTP/1.1 429 Too Many Requests". That is not an exotic case: /auth/otp/request is
        // limited to 12 requests per 10 minutes PER IP (routes/auth.routes.js), so a studio
        // sharing one NAT trips it in the course of an ordinary afternoon and deserves the
        // sentence the server wrote for them.
        private static string ExtractError(DreamParkAPI.APIResponse response)
        {
            if (response == null) return null;
            if (response.json != null && response.json.HasField("error"))
            {
                var errorNode = response.json.GetField("error");
                string msg = null;
                if (errorNode != null)
                {
                    // JSONObject.HasField is false for anything that is not an object, so
                    // the two shapes can be told apart without a type switch.
                    msg = errorNode.HasField("message")
                        ? errorNode.GetField("message").stringValue
                        : errorNode.stringValue;
                }
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
            return string.IsNullOrEmpty(response.error) ? null : response.error;
        }

        // Keeps only digits, up to maxLength. Guards against a paste of "  123 456 "
        // or of a whole sentence out of the email, either of which would otherwise be
        // sent to /auth/otp/verify and rejected as malformed.
        private static string DigitsOnly(string raw, int maxLength)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var buffer = new System.Text.StringBuilder(maxLength);
            for (int i = 0; i < raw.Length && buffer.Length < maxLength; i++)
            {
                if (raw[i] >= '0' && raw[i] <= '9') buffer.Append(raw[i]);
            }
            return buffer.ToString();
        }
    }
}
#endif
