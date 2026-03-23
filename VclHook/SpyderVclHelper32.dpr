{
Copyright 2026 Daniyar Sagatov
Licensed under the Apache License 2.0
}

library SpyderVclHelper32;

uses
  Winapi.Windows,
  System.Classes,
  System.SysUtils,
  System.Types,
  System.TypInfo,
  Vcl.Controls,
  Vcl.Forms,
  Vcl.StdCtrls,
  Vcl.ExtCtrls,
  Vcl.ComCtrls;

var
  GHitInfo: AnsiString;
  GTextOut: AnsiString;
  

function ControlInfo(C: TControl): string;
begin
  if C = nil then
    Exit('nil');
  Result := C.ClassName + '/' + C.Name;
end;

function ResolveHitControl(Root: TWinControl; X, Y: Integer; out Form: TCustomForm): TControl;
var
  ScreenPt: TPoint;
  LocalToRoot: TPoint;
  W: Integer;
  H: Integer;
begin
  Result := nil;
  Form := nil;

  if Root = nil then
    Exit;
  try
    Form := GetParentForm(Root);
    if (Form = nil) and (Root is TCustomForm) then
      Form := TCustomForm(Root);

    LocalToRoot := Point(X, Y);
    W := Root.ClientWidth;
    H := Root.ClientHeight;
    if (W <= 0) or (H <= 0) then
      Exit(Root);
    if (LocalToRoot.X < 0) or (LocalToRoot.Y < 0) or (LocalToRoot.X >= W) or (LocalToRoot.Y >= H) then
      Exit(Root);

    Result := Root.ControlAtPos(LocalToRoot, True, True, True);

    if (Result = nil) and (Form <> nil) then
    begin
      ScreenPt := Root.ClientToScreen(LocalToRoot);
      Result := Vcl.Controls.FindDragTarget(ScreenPt, True);
    end;
  except
    Result := Root;
  end;

  if Result = nil then
    Result := Root;
end;

function Spyder_ControlAtPos(Root: TWinControl; X, Y: Integer): Pointer; stdcall;
var
  C: TControl;
  Form: TCustomForm;
begin
  try
    if Root = nil then
    begin
      Result := nil;
      Exit;
    end;
    C := ResolveHitControl(Root, X, Y, Form);
    Result := Pointer(C);
  except
    Result := Pointer(Root);
  end;
end;

function Spyder_GetHitInfo(Root: TWinControl; X, Y: Integer): PAnsiChar; stdcall;
var
  C: TControl;
  Form: TCustomForm;
  FormText: string;
  HitText: string;
  LocalToRoot: TPoint;
  ScreenPt: TPoint;
begin
  try
    if Root = nil then
    begin
      GHitInfo := 'form=nil; hit=nil; local=0,0; screen=0,0';
      Result := PAnsiChar(GHitInfo);
      Exit;
    end;

    LocalToRoot := Point(X, Y);
    ScreenPt := Root.ClientToScreen(LocalToRoot);
    C := ResolveHitControl(Root, X, Y, Form);
    if Form = nil then
      FormText := 'nil'
    else
      FormText := ControlInfo(Form);
    HitText := ControlInfo(C);

    GHitInfo := AnsiString(
      'form=' + FormText +
      '; hit=' + HitText +
      '; local=' + IntToStr(LocalToRoot.X) + ',' + IntToStr(LocalToRoot.Y) +
      '; screen=' + IntToStr(ScreenPt.X) + ',' + IntToStr(ScreenPt.Y));
    Result := PAnsiChar(GHitInfo);
  except
    GHitInfo := 'form=error; hit=error; local=0,0; screen=0,0';
    Result := PAnsiChar(GHitInfo);
  end;
end;

function Spyder_GetControlRect(C: TControl): TRect; stdcall;
var
  P: TPoint;
begin
  Result := Rect(0, 0, 0, 0);
  try
    if C = nil then
      Exit;
    if (C.Width <= 0) or (C.Height <= 0) then
      Exit;
    if C is TWinControl then
      P := TWinControl(C).ClientToScreen(Point(0, 0))
    else if C.Parent <> nil then
      P := C.Parent.ClientToScreen(Point(C.Left, C.Top))
    else
      Exit;
    Result.Left := P.X;
    Result.Top := P.Y;
    Result.Right := P.X + C.Width;
    Result.Bottom := P.Y + C.Height;
  except
    Result := Rect(0, 0, 0, 0);
  end;
end;

function Spyder_GetComponentName(C: TComponent): PAnsiChar; stdcall;
begin
  try
    if C = nil then
      GTextOut := ''
    else
      GTextOut := AnsiString(C.Name);
    Result := PAnsiChar(GTextOut);
  except
    GTextOut := '';
    Result := PAnsiChar(GTextOut);
  end;
end;

function Spyder_GetCaption(C: TControl): PAnsiChar; stdcall;
var
  PI: PPropInfo;
  S: string;
begin
  try
    S := '';
    if C <> nil then
    begin
      PI := GetPropInfo(C.ClassInfo, 'Caption');
      if PI <> nil then
        S := GetStrProp(C, PI);
    end;
    GTextOut := AnsiString(S);
    Result := PAnsiChar(GTextOut);
  except
    GTextOut := '';
    Result := PAnsiChar(GTextOut);
  end;
end;

function Spyder_GetTabOrder(C: TControl): Integer; stdcall;
begin
  try
    if C is TWinControl then
      Result := TWinControl(C).TabOrder
    else
      Result := -1;
  except
    Result := -1;
  end;
end;

function Spyder_IsEnabled(C: TControl): BOOL; stdcall;
begin
  try
    if C = nil then
      Result := FALSE
    else
      Result := C.Enabled;
  except
    Result := FALSE;
  end;
end;

function Spyder_GetControlType(C: TControl): Integer; stdcall;
var
  N: string;
begin
  try
    if C = nil then Exit(0);
    N := LowerCase(C.ClassName);

    if (Pos('form', N) > 0) then Exit(1);
    if (Pos('button', N) > 0) then Exit(2);
    if (Pos('edit', N) > 0) then Exit(3);
    if (Pos('label', N) > 0) then Exit(4);
    if (Pos('panel', N) > 0) then Exit(5);
    if (Pos('grid', N) > 0) then Exit(6);
    if (Pos('combo', N) > 0) then Exit(7);
    if (Pos('listbox', N) > 0) or (Pos('list', N) > 0) then Exit(8);
    if (Pos('checkbox', N) > 0) or (Pos('check', N) > 0) then Exit(9);
    if (Pos('radio', N) > 0) then Exit(10);
    if (Pos('memo', N) > 0) then Exit(11);
    if (Pos('tab', N) > 0) or (Pos('pagecontrol', N) > 0) then Exit(12);
    if (Pos('toolbar', N) > 0) or (Pos('dxbar', N) > 0) then Exit(13);
    if (Pos('menu', N) > 0) then Exit(14);
    if (Pos('tree', N) > 0) then Exit(15);
    if (Pos('group', N) > 0) then Exit(16);
    Result := 0;
  except
    Result := 0;
  end;
end;

exports
  Spyder_ControlAtPos,
  Spyder_GetHitInfo,
  Spyder_GetControlRect,
  Spyder_GetComponentName,
  Spyder_GetCaption,
  Spyder_GetTabOrder,
  Spyder_IsEnabled,
  Spyder_GetControlType;

begin
end.
