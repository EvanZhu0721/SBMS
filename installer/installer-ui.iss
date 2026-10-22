// Include in [Code]. Requires assets/intro-*.png and README.md (sbms-readme.txt),
// both embedded with dontcopy. Installation and certificate consent stay in SBMS.iss.
type
  TIntroGdiStartup = record
    Version: Cardinal;
    Callback: LongWord;
    SuppressThread, SuppressCodecs: Longint;
  end;
  TIntroMessage = record
    Wnd: HWND;
    Message: Cardinal;
    WParam: LongWord;
    LParam: Longint;
    Time: Cardinal;
    X, Y: Longint;
    PrivateData: Cardinal;
  end;

var
  IntroPage: TWizardPage;
  IntroImage: TBitmapImage;
  ThanksText: TNewMemo;
  IntroTimer: LongWord;
  IntroFrame: Integer;
  IntroStartTime: Int64;
  IntroClockFrequency: Int64;
  IntroPlaying: Boolean;
  IntroFrames: array of TBitmap;
  IntroFinished: Boolean;
  StandardTitleSize, StandardTitleHeight: Integer;
  StandardTitleStyle: TFontStyles;
  InstallProgress: TBitmapImage;
  ProgressFrame: TBitmap;
  ProgressColor, ProgressTrackColor: TColor;

procedure UpdateInstallerProgress(Current, Maximum: Integer);
var
  Bounds: TRect;
begin
  Bounds.Left := 0;
  Bounds.Top := 0;
  Bounds.Right := InstallProgress.Width;
  Bounds.Bottom := InstallProgress.Height;
  ProgressFrame.Canvas.Brush.Color := ProgressTrackColor;
  ProgressFrame.Canvas.FillRect(Bounds);
  if Maximum > 0 then begin
    Bounds.Right := Round((Current * 1.0 / Maximum) * InstallProgress.Width);
    ProgressFrame.Canvas.Brush.Color := ProgressColor;
    ProgressFrame.Canvas.FillRect(Bounds);
  end;
  // Publish the finished frame once; never expose a cleared progress track.
  InstallProgress.Bitmap.Assign(ProgressFrame);
end;

function InstallerSetTimer(Wnd: HWND; ID: LongWord; Interval: Cardinal;
  Callback: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
function InstallerGdiStartup(var Token: LongWord; var Input: TIntroGdiStartup;
  Output: LongWord): Integer;
  external 'GdiplusStartup@gdiplus.dll stdcall';
procedure InstallerGdiShutdown(Token: LongWord);
  external 'GdiplusShutdown@gdiplus.dll stdcall';
function InstallerLoadPng(FileName: String; var Image: LongWord): Integer;
  external 'GdipCreateBitmapFromFile@gdiplus.dll stdcall';
function InstallerFlattenPng(Image: LongWord; var Bitmap: HBITMAP; Background: Cardinal): Integer;
  external 'GdipCreateHBITMAPFromBitmap@gdiplus.dll stdcall';
function InstallerDisposePng(Image: LongWord): Integer;
  external 'GdipDisposeImage@gdiplus.dll stdcall';
function InstallerKillTimer(Wnd: HWND; ID: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';
function InstallerCounter(var Value: Int64): Boolean;
  external 'QueryPerformanceCounter@kernel32.dll stdcall';
function InstallerCounterFrequency(var Value: Int64): Boolean;
  external 'QueryPerformanceFrequency@kernel32.dll stdcall';
function InstallerCreateFrameTimer(Attributes: LongWord; Name: LongWord;
  Flags, Access: Cardinal): THandle;
  external 'CreateWaitableTimerExW@kernel32.dll stdcall';
function InstallerArmFrameTimer(Timer: THandle; var Due: Int64; Period: Longint;
  Callback, Context: LongWord; Resume: Boolean): Boolean;
  external 'SetWaitableTimer@kernel32.dll stdcall';
function InstallerWaitForFrame(Count: Cardinal; var Timer: THandle;
  Timeout, WakeMask, Flags: Cardinal): Cardinal;
  external 'MsgWaitForMultipleObjectsEx@user32.dll stdcall';
function InstallerCloseFrameTimer(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function InstallerPeekMessage(var Msg: TIntroMessage; Wnd: HWND;
  First, Last, Remove: Cardinal): Boolean;
  external 'PeekMessageW@user32.dll stdcall';
function InstallerDialogMessage(Wnd: HWND; var Msg: TIntroMessage): Boolean;
  external 'IsDialogMessageW@user32.dll stdcall';
function InstallerTranslateMessage(var Msg: TIntroMessage): Boolean;
  external 'TranslateMessage@user32.dll stdcall';
function InstallerDispatchMessage(var Msg: TIntroMessage): Longint;
  external 'DispatchMessageW@user32.dll stdcall';
procedure InstallerPostQuitMessage(Code: Integer);
  external 'PostQuitMessage@user32.dll stdcall';
function InstallerGetWindowLong(Wnd: HWND; Index: Integer): Longint;
  external 'GetWindowLongW@user32.dll stdcall';
function InstallerSetWindowLong(Wnd: HWND; Index: Integer; Value: Longint): Longint;
  external 'SetWindowLongW@user32.dll stdcall';
function InstallerSetTextRect(Wnd: HWND; Msg: Cardinal; WParam: LongWord;
  var Rect: TRect): Longint;
  external 'SendMessageW@user32.dll stdcall';

procedure FreeInstallerIntroFrames;
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(IntroFrames) - 1 do
    if IntroFrames[I] <> nil then IntroFrames[I].Free;
  SetArrayLength(IntroFrames, 0);
end;

procedure StopInstallerIntro;
begin
  if IntroPlaying or (IntroTimer <> 0) then FreeInstallerIntroFrames;
  IntroPlaying := False;
  if IntroTimer <> 0 then begin
    InstallerKillTimer(0, IntroTimer);
    IntroTimer := 0;
  end;
end;

procedure ShowInstallerThanks;
begin
  StopInstallerIntro;
  IntroFinished := True;
  IntroImage.Visible := False;
  ThanksText.Visible := True;
  WizardForm.PageNameLabel.Caption := 'Acknowledgments';
end;

function InstallerIntroElapsed: Double;
var
  Counter: Int64;
begin
  InstallerCounter(Counter);
  Result := (Counter - IntroStartTime) * 1000.0 / IntroClockFrequency;
end;

procedure SetInstallerIntroFrame(Frame: Integer);
begin
  if Frame = IntroFrame then exit;
  IntroFrame := Frame;
  IntroImage.Bitmap.Assign(IntroFrames[Frame]);
end;

procedure InstallerIntroHoldTick(Wnd: HWND; Msg: Cardinal; ID: LongWord; Time: Cardinal);
begin
  if InstallerIntroElapsed >= 2440 then ShowInstallerThanks;
end;

procedure InstallerIntroTick(Wnd: HWND; Msg: Cardinal; ID: LongWord; Time: Cardinal);
var
  FrameTimer: THandle;
  Due: Int64;
  Elapsed, Delay: Double;
  Frame: Integer;
  Message: TIntroMessage;
begin
  // SetTimer only starts playback after the page is visible. Its 10 ms minimum
  // cannot schedule 120 FPS. A high-resolution waitable timer wakes this UI
  // thread; the message pump keeps skip, cancel, keyboard and paint responsive.
  InstallerKillTimer(0, IntroTimer);
  IntroTimer := 0;
  FrameTimer := InstallerCreateFrameTimer(0, 0, 2, $001F0003);
  if FrameTimer = 0 then RaiseException('Unable to create installer animation timer.');
  IntroPlaying := True;
  InstallerCounter(IntroStartTime);
  try
    while IntroPlaying do begin
      while InstallerPeekMessage(Message, 0, 0, 0, 1) do begin
        if Message.Message = $0012 then begin
          IntroPlaying := False;
          InstallerPostQuitMessage(Message.WParam);
          Break;
        end;
        if not InstallerDialogMessage(WizardForm.Handle, Message) then begin
          InstallerTranslateMessage(Message);
          InstallerDispatchMessage(Message);
        end;
        if not IntroPlaying then Break;
      end;
      if not IntroPlaying then Break;
      Elapsed := InstallerIntroElapsed;
      if Elapsed >= 760 then begin
        SetInstallerIntroFrame(92);
        Break;
      end;
      Frame := Trunc(Elapsed * 120 / 1000);
      SetInstallerIntroFrame(Frame);
      // Absolute timeline: slow paints skip ahead, never stretch the animation.
      Delay := ((Frame + 1) * 1000.0 / 120) - InstallerIntroElapsed;
      if Delay < 0.1 then Delay := 0.1;
      Due := -Round(Delay * 10000);
      if not InstallerArmFrameTimer(FrameTimer, Due, 0, 0, 0, False) then
        RaiseException('Unable to schedule installer animation frame.');
      InstallerWaitForFrame(1, FrameTimer, $FFFFFFFF, $04FF, $0004);
    end;
  finally
    InstallerCloseFrameTimer(FrameTimer);
  end;
  if IntroPlaying then begin
    IntroPlaying := False;
    FreeInstallerIntroFrames;
    // Final logo holds for 1.68 s; no high-frequency wakeups during this hold.
    IntroTimer := InstallerSetTimer(0, 0, 25, CreateCallback(@InstallerIntroHoldTick));
  end;
end;

function ReadInstallerThanks: String;
var
  Lines: TArrayOfString;
  I: Integer;
  Reading: Boolean;
begin
  Result := '';
  Reading := False;
  ExtractTemporaryFile('sbms-readme.txt');
  if LoadStringsFromFile(ExpandConstant('{tmp}\sbms-readme.txt'), Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do begin
      if Lines[I] = '## 致谢' then
        Reading := True
      else if Reading then begin
        if Pos('## ', Lines[I]) = 1 then Break;
        if Trim(Lines[I]) = 'First of all, thank you for taking the time to read this far.' then
          Result := Result + 'Thank you for trying SBMS' + #13#10
        else if (Trim(Lines[I]) <> 'BTW I subscribe CodexPro500 XD') and
          (Pos('$${', Lines[I]) <> 1) then
          Result := Result + Trim(Lines[I]) + #13#10;
      end;
    end;
  Result := Trim(Result);
end;

procedure InitializeInstallerUI;
var
  I: Integer;
  LightTheme: Cardinal;
  TextRect: TRect;
  GdiInput: TIntroGdiStartup;
  GdiToken, Source: LongWord;
  BitmapHandle: HBITMAP;
  Background: Cardinal;
begin
  InstallerCounterFrequency(IntroClockFrequency);
  IntroPage := CreateCustomPage(wpWelcome, 'SBMS', '');
  // TBitmapImage erases its background before drawing the transparent PNG.
  // Composite this page offscreen so those two operations appear as one frame.
  // Keep it scoped here: compositing the whole wizard disrupts styled memo paint.
  InstallerSetWindowLong(IntroPage.Surface.Handle, -20,
    InstallerGetWindowLong(IntroPage.Surface.Handle, -20) or $02000000);
  WizardForm.WizardSmallBitmapImage.Visible := False;
  StandardTitleSize := WizardForm.PageNameLabel.Font.Size;
  StandardTitleHeight := WizardForm.PageNameLabel.Height;
  StandardTitleStyle := WizardForm.PageNameLabel.Font.Style;
  IntroImage := TBitmapImage.Create(WizardForm);
  IntroImage.Parent := IntroPage.Surface;
  IntroImage.SetBounds((IntroPage.SurfaceWidth - ScaleX(480)) div 2,
    ScaleY(30), ScaleX(480), ScaleY(200));
  IntroImage.Center := True;
  IntroImage.Stretch := True;
  IntroImage.BackColor := $F9F3F8;
  if RegQueryDWordValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize',
    'AppsUseLightTheme', LightTheme) and (LightTheme = 0) then
    IntroImage.BackColor := $2D282A;
#ifdef InstallerPreviewDark
  IntroImage.BackColor := $2D282A;
#endif
#ifdef InstallerPreviewLight
  IntroImage.BackColor := $F9F3F8;
#endif
  // Decode and flatten alpha once before showing the wizard. During playback,
  // bitmap copies avoid per-frame PNG decoding and software alpha blending.
  // Keep the original 960x400 resolution for high-DPI screens; release at hold.
  SetArrayLength(IntroFrames, 93);
  GdiInput.Version := 1;
  GdiInput.Callback := 0;
  GdiInput.SuppressThread := 0;
  GdiInput.SuppressCodecs := 0;
  if InstallerGdiStartup(GdiToken, GdiInput, 0) <> 0 then
    RaiseException('Unable to prepare installer animation.');
  Background := $FF000000 or ((IntroImage.BackColor and $FF) shl 16) or
    (IntroImage.BackColor and $FF00) or ((IntroImage.BackColor shr 16) and $FF);
  try
    for I := 0 to 92 do begin
      ExtractTemporaryFile('intro-' + Format('%.2d', [I]) + '.png');
      if InstallerLoadPng(ExpandConstant('{tmp}\intro-') + Format('%.2d', [I]) + '.png', Source) <> 0 then
        RaiseException('Unable to decode installer animation.');
      try
        if InstallerFlattenPng(Source, BitmapHandle, Background) <> 0 then
          RaiseException('Unable to prepare installer animation bitmap.');
        IntroFrames[I] := TBitmap.Create;
        IntroFrames[I].Handle := BitmapHandle;
      finally
        InstallerDisposePng(Source);
      end;
    end;
  finally
    InstallerGdiShutdown(GdiToken);
  end;
  IntroImage.Bitmap.Assign(IntroFrames[0]);
  ThanksText := TNewMemo.Create(WizardForm);
  ThanksText.Parent := IntroPage.Surface;
  ThanksText.SetBounds(0, 0, IntroPage.SurfaceWidth, IntroPage.SurfaceHeight);
  ThanksText.ReadOnly := True;
  ThanksText.BorderStyle := bsNone;
  ThanksText.ScrollBars := ssVertical;
  ThanksText.Font.Name := 'Segoe UI';
  ThanksText.Font.Size := 11;
  ThanksText.Color := IntroImage.BackColor;
  ThanksText.Text := ReadInstallerThanks;
  TextRect.Left := ScaleX(16);
  TextRect.Top := ScaleY(16);
  TextRect.Right := ThanksText.Width - ScaleX(32);
  TextRect.Bottom := ThanksText.Height - ScaleY(16);
  InstallerSetTextRect(ThanksText.Handle, $00B3, 0, TextRect);
  ThanksText.Visible := False;

  InstallProgress := TBitmapImage.Create(WizardForm);
  InstallProgress.Parent := WizardForm.InstallingPage;
  InstallProgress.SetBounds(WizardForm.ProgressGauge.Left,
    WizardForm.ProgressGauge.Top, WizardForm.ProgressGauge.Width, ScaleY(6));
  ProgressColor := $A45067;
  ProgressTrackColor := $FFDDEA;
  if IntroImage.BackColor = $2D282A then begin
    ProgressColor := $FFBCD0;
    ProgressTrackColor := $8B374F;
  end;
  ProgressFrame := TBitmap.Create;
  ProgressFrame.Width := InstallProgress.Width;
  ProgressFrame.Height := InstallProgress.Height;
  UpdateInstallerProgress(0, 1);
  WizardForm.ProgressGauge.Visible := False;
end;

procedure PlaceInstallerThanks(Parent: TWinControl; Top, Width, Height: Integer);
var
  TextRect: TRect;
begin
  ThanksText.Parent := Parent;
  ThanksText.SetBounds(0, Top, Width, Height);
  TextRect.Left := ScaleX(16);
  TextRect.Top := ScaleY(16);
  TextRect.Right := Width - ScaleX(32);
  TextRect.Bottom := Height - ScaleY(16);
  InstallerSetTextRect(ThanksText.Handle, $00B3, 0, TextRect);
end;

procedure InstallerUIPageChanged(PageID: Integer);
var
  ThanksTop: Integer;
begin
  if PageID = IntroPage.ID then begin
    PlaceInstallerThanks(IntroPage.Surface, 0,
      IntroPage.SurfaceWidth, IntroPage.SurfaceHeight);
    WizardForm.PageNameLabel.Font.Size := 18;
    WizardForm.PageNameLabel.Font.Style := [];
    WizardForm.PageNameLabel.Height := ScaleY(34);
    WizardForm.PageDescriptionLabel.Visible := False;
    if IntroFinished then
      ShowInstallerThanks
    else if IntroTimer = 0 then begin
      // Kick off once the wizard has entered its ordinary message loop.
      IntroTimer := InstallerSetTimer(0, 0, 10, CreateCallback(@InstallerIntroTick));
    end;
  end else begin
    StopInstallerIntro;
    WizardForm.PageNameLabel.Font.Size := StandardTitleSize;
    WizardForm.PageNameLabel.Font.Style := StandardTitleStyle;
    WizardForm.PageNameLabel.Height := StandardTitleHeight;
    WizardForm.PageDescriptionLabel.Visible := True;
    if PageID = wpInstalling then begin
      ThanksTop := InstallProgress.Top + InstallProgress.Height + ScaleY(24);
      PlaceInstallerThanks(WizardForm.InstallingPage, ThanksTop,
        WizardForm.InstallingPage.Width, WizardForm.InstallingPage.Height - ThanksTop);
      ThanksText.Visible := True;
      WizardForm.ProgressGauge.Visible := False;
    end;
  end;
end;

function InstallerUINextButtonClick(PageID: Integer): Boolean;
begin
  Result := True;
  if WizardSilent then exit;
  if (PageID = IntroPage.ID) and not IntroFinished then begin
    ShowInstallerThanks;
    Result := False;
  end;
end;

procedure DeinitializeInstallerUI;
begin
  StopInstallerIntro;
  FreeInstallerIntroFrames;
  if ProgressFrame <> nil then ProgressFrame.Free;
end;
