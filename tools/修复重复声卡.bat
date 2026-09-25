@echo off
title Fix duplicate virtual sound card
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0fix-duplicate-soundcard.ps1'"
