@echo off
rem Tanak omotac - VBS iz Startup foldera poziva ovaj fajl.
rem Sva logika je u scripts\PokreniBota.ps1 da bi bila deo repozitorijuma.
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0scripts\PokreniBota.ps1"
