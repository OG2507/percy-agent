@echo off
REM ── Ask Percy: the brain, in a window. Type questions, get answers. ──
REM He fetches live facts from every registered station (Baldrick, the GPU,
REM ComfyUI, Percy-Voice...) and only ever speaks what he just fetched.
REM Runs entirely on CPU - never touches the 5090.
REM If replies come back spoken, Percy-Voice is up and the card is free.
title Percy
cd /d "C:\GIT\percy-agent\brain"
python percy_brain.py
pause >nul
