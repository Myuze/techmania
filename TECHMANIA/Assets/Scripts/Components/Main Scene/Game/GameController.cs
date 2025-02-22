using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class GameController : MonoBehaviour
{
    public static GameController instance { get; private set; }

    public VisualTreeAsset layoutTemplate;
    public VisualTreeAsset scanlineTemplate;

    [Serializable]
    public class NoteTemplates
    {
        public VisualTreeAsset basicNote;
        public VisualTreeAsset chainHead;
        public VisualTreeAsset chainNode;
        public VisualTreeAsset dragNote;
        public VisualTreeAsset holdNote;
        public VisualTreeAsset holdExtension;
        public VisualTreeAsset repeatHead;
        public VisualTreeAsset repeatHeadHold;
        public VisualTreeAsset repeatNote;
        public VisualTreeAsset repeatHold;
        public VisualTreeAsset repeatHoldExtension;
        public VisualTreeAsset repeatPathExtension;

        public VisualTreeAsset GetForType(NoteType type)
        {
            return type switch
            {
                NoteType.Basic => basicNote,
                NoteType.ChainHead => chainHead,
                NoteType.ChainNode => chainNode,
                NoteType.Drag => dragNote,
                NoteType.Hold => holdNote,
                NoteType.RepeatHead => repeatHead,
                NoteType.RepeatHeadHold => repeatHeadHold,
                NoteType.Repeat => repeatNote,
                NoteType.RepeatHold => repeatHold,
                _ => null
            };
        }

        public VisualTreeAsset GetHoldExtensionForType(NoteType type)
        {
            return type switch
            {
                NoteType.Hold => holdExtension,
                NoteType.RepeatHeadHold => repeatHoldExtension,
                NoteType.RepeatHold => repeatHoldExtension,
                _ => null
            };
        }
    }
    public NoteTemplates noteTemplates;
    public VisualTreeAsset inputFeedbackTemplate;

    private ThemeApi.GameSetup setup;
    private ThemeApi.GameState state;
    public Modifiers modifiers => setup.modifiers;
    // Accessible from Lua via GameState.timer
    public GameTimer timer { get; private set; }
    private GameBackground bg;
    private KeysoundPlayer keysoundPlayer;
    private GameLayout layout;
    private NoteManager noteManager;
    private GameInputManager input;
    private InputFeedbackManager inputFeedback;
    // Accessible from Lua via GameState.scoreKeeper
    public ScoreKeeper scoreKeeper { get; private set; }

    public VFXManager vfxManager;
    public ComboText comboText;

    [HideInInspector]
    public bool autoPlay;
    [HideInInspector]
    public bool showHitbox;

    public void SetSetupInstance(ThemeApi.GameSetup s)
    {
        setup = s;
    }
    public void SetStateInstance(ThemeApi.GameState s)
    {
        state = s;
    }

    // Start is called before the first frame update
    void Start()
    {
        instance = this;
    }

    private IEnumerator LoadSequence()
    {
        Action<Status> reportLoadError = (Status status) =>
        {
            state.SetLoadError();
            setup.onLoadError?.Function?.Call(status);
        };
        int filesLoaded = 0;
        int totalFiles = 0;
        Action<string> reportLoadProgress = (string fileJustLoaded) =>
        {
            filesLoaded++;
            setup.onLoadProgress?.Function?.Call(
                new ThemeApi.GameSetup.LoadProgress()
                {
                    fileJustLoaded = fileJustLoaded,
                    filesLoaded = filesLoaded,
                    totalFiles = totalFiles
                });
        };

        // Reload custom ruleset if using it.
        if (Options.instance.ruleset == Options.Ruleset.Custom)
        {
            try
            {
                Ruleset.LoadCustomRuleset();
            }
            catch (Exception) { /* silently ignore errors */ }
        }

        // Load track, track options and pattern. These are all
        // synchronous.
        if (EditorContext.inPreview)
        {
            setup.trackOptions = new PerTrackOptions();
            setup.patternBeforeModifier = EditorContext.Pattern;
            setup.patternAfterModifier = EditorContext.Pattern;
        }
        else
        {
            string trackPath = Paths.Combine(setup.lockedTrackFolder,
                Paths.kTrackFilename);
            Track track;
            try
            {
                track = Track.LoadFromFile(trackPath) as Track;
            }
            catch (Exception ex)
            {
                reportLoadError(Status.FromException(ex));
                yield break;
            }

            setup.trackOptions = Options.instance.GetPerTrackOptions(
                track.trackMetadata.guid);

            setup.patternBeforeModifier = null;
            foreach (Pattern p in track.patterns)
            {
                if (p.patternMetadata.guid == setup.patternGuid)
                {
                    setup.patternBeforeModifier = p;
                    break;
                }
            }
            if (setup.patternBeforeModifier == null)
            {
                reportLoadError(Status.Error(
                    Status.Code.NotFound,
                    "The specified pattern is not found in the track."));
                yield break;
            }
            setup.patternAfterModifier = setup.patternBeforeModifier
                .ApplyModifiers(setup.modifiers);
        }

        // Calculate fingerprints in preparation for records.
        setup.patternBeforeModifier.CalculateFingerprint();
        setup.patternAfterModifier.CalculateFingerprint();

        // Step 0: calculate the number of files to load.
        totalFiles =
            1 +  // Background image
            1 +  // Skins
            1 +  // Backing track
            0 +  // Keysounds
            1;  // BGA
        HashSet<string> keysoundFullPaths = new HashSet<string>();
        foreach (Note n in setup.patternAfterModifier.notes)
        {
            if (n.sound != null && n.sound != "")
            {
                keysoundFullPaths.Add(Paths.Combine(
                    setup.lockedTrackFolder, n.sound));
            }
        }
        totalFiles += keysoundFullPaths.Count;

        // Step 1: load background image to display on the loading
        // screen.
        yield return null;  // Delay 1 frame for layout update
        bg = new GameBackground(
            setup.patternAfterModifier,
            setup.bgContainer.inner,
            trackOptions: setup.trackOptions);
        string backImage = setup.patternAfterModifier.patternMetadata
            .backImage;
        if (!string.IsNullOrEmpty(backImage))
        {
            string path = Paths.Combine(
                setup.lockedTrackFolder, backImage);
            bool loaded = false;
            Status status = null;
            Texture2D texture = null;
            ResourceLoader.LoadImage(path,
                (Status loadStatus, Texture2D loadedTexture) =>
                {
                    loaded = true;
                    status = loadStatus;
                    texture = loadedTexture;
                });
            yield return new WaitUntil(() => loaded);
            if (!status.Ok())
            {
                reportLoadError(status);
                yield break;
            }
            bg.DisplayImage(texture);
        }
        reportLoadProgress(backImage);

        // Step 2: load skins, if told to.
        if (Options.instance.reloadSkinsWhenLoadingPattern)
        {
            bool loaded = false;
            Status status = null;
            GlobalResourceLoader.GetInstance().LoadAllSkins(
                progressCallback: null,
                completeCallback: (Status loadStatus) =>
                {
                    loaded = true;
                    status = loadStatus;
                });
            yield return new WaitUntil(() => loaded);
            if (!status.Ok())
            {
                reportLoadError(status);
                yield break;
            }
        }
        reportLoadProgress(Paths.kSkinFilename);

        // Step 3: load backing track.
        string backingTrackFilename = setup.patternAfterModifier
            .patternMetadata.backingTrack;
        if (!string.IsNullOrEmpty(backingTrackFilename))
        {
            string path = Paths.Combine(setup.lockedTrackFolder,
                backingTrackFilename);
            bool loaded = false;
            Status status = null;
            AudioClip clip = null;
            ResourceLoader.LoadAudio(path,
                (Status loadStatus, AudioClip loadedClip) =>
                {
                    loaded = true;
                    status = loadStatus;
                    clip = loadedClip;
                });
            yield return new WaitUntil(() => loaded);
            if (!status.Ok())
            {
                reportLoadError(status);
                yield break;
            }
            bg.SetBackingTrack(clip);
        }
        reportLoadProgress(backingTrackFilename);

        // Step 4: load keysounds.
        bool keysoundsLoaded = false;
        Status keysoundStatus = null;
        ResourceLoader.CacheAllKeysounds(setup.lockedTrackFolder,
            keysoundFullPaths,
            cacheAudioCompleteCallback: (Status status) =>
            {
                keysoundsLoaded = true;
                keysoundStatus = status;
            },
            fileLoadedCallback: (string fileJustLoaded) =>
            {
                reportLoadProgress(fileJustLoaded);
            });
        yield return new WaitUntil(() => keysoundsLoaded);
        if (!keysoundStatus.Ok())
        {
            reportLoadError(keysoundStatus);
            yield break;
        }

        // Step 5: load BGA.
        string bga = setup.patternAfterModifier.patternMetadata.bga;
        if (!string.IsNullOrEmpty(bga) &&
            !setup.trackOptions.noVideo)
        {
            string path = Paths.Combine(setup.lockedTrackFolder,
                bga);
            bool loaded = false;
            Status status = null;
            ThemeApi.VideoElement element = null;
            ThemeApi.VideoElement.CreateFromFile(path,
                (Status loadStatus,
                ThemeApi.VideoElement loadedElement) =>
                {
                    loaded = true;
                    status = loadStatus;
                    element = loadedElement;
                });
            yield return new WaitUntil(() => loaded);
            if (!status.Ok())
            {
                reportLoadError(status);
                yield break;
            }
            bg.SetBga(element,
                loop: setup.patternAfterModifier.patternMetadata
                    .playBgaOnLoop,
                offset: (float)setup.patternAfterModifier
                    .patternMetadata.bgaOffset);
        }
        reportLoadProgress(bga);

        // A few more synchronous loading steps.

        // Switches.
        autoPlay = setup.modifiers.mode == Modifiers.Mode.AutoPlay;
        showHitbox = false;

        // Keysound player.
        keysoundPlayer = new KeysoundPlayer(setup.assistTick);
        keysoundPlayer.Prepare();

        // Prepare timer.
        timer = new GameTimer(setup.patternAfterModifier);
        float backingTrackLength = 0f;
        float bgaLength = 0f;
        if (bg.backingTrack != null)
        {
            backingTrackLength = bg.backingTrack.length;
        }
        if (bg.bgaElement != null &&
            !setup.patternAfterModifier
                .patternMetadata.playBgaOnLoop &&
            setup.patternAfterModifier.patternMetadata.waitForEndOfBga)
        {
            bgaLength = bg.bgaElement.length;
        }
        timer.Prepare(backingTrackLength, bgaLength);

        // Prepare scanlines and scan countdowns.
        layout = new GameLayout(
            pattern: setup.patternAfterModifier,
            gameContainer: setup.gameContainer.inner,
            layoutTemplate: layoutTemplate);
        layout.Prepare(
            firstScan: timer.firstScan,
            lastScan: timer.lastScan,
            scanlineTemplate);
        yield return null;  // For layout update
        layout.ResetSize();

        // Spawn notes.
        noteManager = new NoteManager(layout);
        noteManager.Prepare(
            setup.patternAfterModifier,
            noteTemplates);

        // Set up bg to play hidden notes.
        bg.SetNoteManager(noteManager, keysoundPlayer,
            playableLanes: setup.patternAfterModifier
                .patternMetadata.playableLanes);

        // Prepare for input.
        input = new GameInputManager(setup.patternAfterModifier,
            this, layout, noteManager, timer);
        input.Prepare();

        // Prepare for input feedback.
        inputFeedback = new InputFeedbackManager(
            inputFeedbackTemplate, layout, input);
        inputFeedback.Prepare(
            setup.patternAfterModifier.patternMetadata);

        // Prepare for VFX and combo text.
        vfxManager.Prepare(layout.laneHeight, timer, layout);
        comboText.ResetSize(layout.scanHeight);
        comboText.Hide();

        // Initialize scores.
        scoreKeeper = new ScoreKeeper(setup);
        scoreKeeper.Prepare(setup.patternAfterModifier,
            timer.firstScan, timer.lastScan,
            playableNotes: noteManager.playableNotes);

        // Load complete; wait on theme to begin game.
        state.SetLoadComplete();
        setup.onLoadComplete?.Function?.Call();
    }

    #region State machine
    public void BeginLoading()
    {
        // Lock down certain fields so the theme can't
        // change them later.
        //
        // No need to lock down the actual track and patterns yet,
        // as the theme cannot modify them on disk yet.
        if (EditorContext.inPreview)
        {
            setup.lockedTrackFolder = string.Copy(
                EditorContext.trackFolder);
            setup.modifiers = new Modifiers()
            {
                mode = Modifiers.Mode.Practice
            };
        }
        else
        {
            setup.lockedTrackFolder = string.Copy(setup.trackFolder);
            setup.modifiers = Modifiers.instance.Clone();
        }
        setup.ruleset = Options.instance.ruleset;

        StartCoroutine(LoadSequence());
    }

    public void Begin()
    {
        timer.Begin();
        bg.Begin();
        layout.Begin();

        if (EditorContext.inPreview)
        {
            JumpToScan(EditorContext.previewStartingScan);
        }
        else
        {
            JumpToScan(timer.firstScan);
        }
    }

    public void Pause()
    {
        timer.Pause();
        bg.Pause();
        keysoundPlayer.Pause();
        scoreKeeper.Pause();
    }

    public void Unpause()
    {
        timer.Unpause();
        bg.Unpause();
        keysoundPlayer.Unpause();
        scoreKeeper.Unpause();
    }

    public void Conclude()
    {
        AudioSourceManager.instance.SetSpeed(1f);

        timer.Dispose();
        bg.Conclude();
        keysoundPlayer.Dispose();
        layout.Dispose();
        noteManager.Dispose();
        input.Dispose();
        vfxManager.Dispose();
        comboText.Hide();
    }
    #endregion

    #region APIs available in Complete state
    public void StopAllGameAudio()
    {
        bg.StopBackingTrack();
        keysoundPlayer.StopAll();
    }

    public void StopBga()
    {
        bg.StopBga();
    }

    public bool ScoreIsValid()
    {
        return !setup.modifiers.HasAnySpecialModifier() &&
            setup.ruleset != Options.Ruleset.Custom &&
            !scoreKeeper.stageFailed;
    }

    public bool ScoreIsNewRecord()
    {
        if (!ScoreIsValid()) return false;
        Record currentRecord = Records.instance.GetRecord(
            setup.patternBeforeModifier, setup.ruleset);
        if (currentRecord == null) return true;
        int currentScore = currentRecord.score;
        int newScore = scoreKeeper.TotalScore();

        return newScore > currentScore;
    }

    public void UpdateRecord()
    {
        if (!ScoreIsValid()) return;
        Records.instance.UpdateRecord(
            setup.patternBeforeModifier,
            setup.ruleset,
            scoreKeeper.TotalScore(),
            scoreKeeper.Medal());
    }
    #endregion

    #region Other theme APIs
    public void UpdateBgBrightness()
    {
        bg.UpdateBgBrightness();
    }

    public void ResetElementSizes()
    {
        layout.ResetSize();
        noteManager.ResetSize();
        vfxManager.ResetSize(layout.laneHeight);
        comboText.ResetSize(layout.scanHeight);
    }

    public void ActivateFever()
    {
        scoreKeeper.ActivateFever();
    }

    public void SetSpeed(int speedPercent)
    {
        if (speedPercent <= 0)
        {
            throw new Exception("Cannot set game speed to 0 or negative.");
        }

        // It's up to GameState.SetSpeed to check if we are in
        // practice mode.
        timer.SetSpeed(speedPercent);
        AudioSourceManager.instance.SetSpeed(timer.speed);
        bg.SetBgaSpeed(timer.speed);
    }
    #endregion

    #region Jumping scans
    public void JumpToScan(int scan)
    {
        // Clamp scan into bounds.
        scan = Mathf.Clamp(scan, timer.firstScan, timer.lastScan);

        // Update components.
        timer.JumpToScan(scan);
        bg.Seek(timer.baseTime);
        noteManager.JumpToScan(timer.intScan, timer.intPulse);
        input.JumpToScan();
        scoreKeeper.JumpToScan();
        vfxManager.JumpToScan();

        // Play keysounds before the current time if they last enough.
        keysoundPlayer.StopAll();
        foreach (NoteList l in noteManager.notesInLane)
        {
            l.ForEachRemoved((INoteHolder holder) =>
            {
                keysoundPlayer.PlayFromHalfway(holder.note,
                    hidden: setup.patternAfterModifier.IsHidden(
                        holder.note.lane),
                    timer.baseTime);
            });
        }
    }
    #endregion

    #region Update
    // Update is called once per frame
    void Update()
    {
        if (state == null) return;

        if (state.state == ThemeApi.GameState.State.Paused)
        {
            // Input feedbacks should work through pauses as fingers
            // may enter and leave during a pause.
            inputFeedback.Update(timer.scan);
        }
        else if (state.state == ThemeApi.GameState.State.Ongoing)
        {
            timer.Update(comboTickCallback: ComboTick);
            bg.Update(timer.baseTime, timer.prevFrameBaseTime);
            layout.Update(timer.scan);
            noteManager.Update(timer, scoreKeeper);
            input.Update();
            inputFeedback.Update(timer.scan);
            scoreKeeper.UpdateFever();

            CheckForStageFailed();
            CheckForStageClear();

            if (state.state != ThemeApi.GameState.State.Complete)
            {
                // If game hasn't completed from stage failed or
                // stage clear, call the update callback.
                setup.onUpdate?.Function?.Call(timer);
            }
        }
    }

    private void CheckForStageFailed()
    {
        if (scoreKeeper.hp <= 0 &&
            setup.modifiers.mode != Modifiers.Mode.NoFail)
        {
            scoreKeeper.stageFailed = true;
            state.SetComplete();
            setup.onStageFailed?.Function?.Call(scoreKeeper);
        }
    }

    private void CheckForStageClear()
    {
        if (state.state != ThemeApi.GameState.State.Ongoing) return;
        if (timer.baseTime <= timer.patternEndTime) return;
        if (setup.modifiers.mode == Modifiers.Mode.Practice) return;
        if (!scoreKeeper.AllNotesResolved()) return;

        scoreKeeper.DeactivateFever();
        state.SetComplete();
        setup.onStageClear?.Function?.Call(scoreKeeper);
    }
    #endregion

    #region Responding to input
    public void HitNote(NoteElements elements, float timeDifference)
    {
        Judgement judgement = GameInputManager
            .TimeDifferenceToJudgement(elements.note,
            timeDifference, timer.speed);

        switch (elements.note.type)
        {
            case NoteType.Hold:
            case NoteType.Drag:
            case NoteType.RepeatHeadHold:
            case NoteType.RepeatHold:
                if (judgement == Judgement.Miss)
                {
                    // Missed notes do not enter Ongoing state.
                    ResolveNote(elements, judgement);
                }
                else
                {
                    // Register an ongoing note.
                    input.RegisterOngoingNote(elements, judgement);
                    elements.SetOngoing();
                    vfxManager.SpawnOngoingVFX(elements, judgement);
                }
                break;
            default:
                ResolveNote(elements, judgement);
                break;
        }

        keysoundPlayer.Play(elements.note,
            hidden: false, emptyHit: false);
    }

    public void ResolveNote(NoteElements elements,
        Judgement judgement)
    {
        noteManager.ResolveNote(elements);
        scoreKeeper.ResolveNote(elements.note.type, judgement);
        vfxManager.SpawnResolvedVFX(elements, judgement);
        comboText.Show(elements.noteImage, judgement, scoreKeeper);
        elements.Resolve();

        setup.onNoteResolved?.Function?.Call(elements.note,
            judgement, scoreKeeper);
        if (scoreKeeper.AllNotesResolved())
        {
            setup.onAllNotesResolved?.Function?.Call(scoreKeeper);
        }
    }

    public void StopKeysoundIfPlaying(Note n)
    {
        keysoundPlayer.StopIfPlaying(n);
    }

    public void EmptyHitForFinger(int lane)
    {
        if (lane == GameLayout.kOutsideAllLanes)
        {
            return;
        }

        // Find the upcoming note.
        NoteElements upcomingNote = noteManager.GetUpcoming(
            lane,
            setup.patternAfterModifier.patternMetadata.controlScheme);

        if (upcomingNote == null) return;
        if (input.IsOngoing(upcomingNote)) return;
        keysoundPlayer.Play(upcomingNote.note,
            hidden: false, emptyHit: true);
    }

    public void EmptyHitForKeyboard(Note note)
    {
        keysoundPlayer.Play(note,
            hidden: false, emptyHit: true);
    }
    #endregion

    #region Responding to time
    public void ComboTick()
    {
        foreach (KeyValuePair<NoteElements, Judgement> pair in
            input.ongoingNotes)
        {
            NoteElements elements = pair.Key;
            Judgement judgement = pair.Value;
            scoreKeeper.IncrementCombo();
            comboText.Show(elements.noteImage, judgement, scoreKeeper);

            setup.onComboTick?.Function.Call(scoreKeeper.currentCombo);
        }
    }
    #endregion
}
