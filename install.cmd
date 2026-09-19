@echo off
title BgFree installer
echo BgFree (HDT Battlegrounds plugin) installing...
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/Heinul/Hearthstone_Deck_Tracker_Battlegrounds_Plugin/main/install.ps1 | iex"
echo.
pause
