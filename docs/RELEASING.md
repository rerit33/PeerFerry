# Релизы и обновления

В первой итерации публикуются исходники; установщик и portable-сборка остаются локально. GitHub workflow на push/PR собирает и проверяет код. Ручной запуск Windows workflow дополнительно создаёт артефакты сборки, не публикуя Release.

1. Обновить версию по умолчанию в `Directory.Build.props` и `scripts/build.ps1`.
2. Выполнить `pwsh -File scripts/build.ps1 -Version 0.2.0`.
3. Проверить установщик/portable и сценарии из `TESTING.md`; сохранить SHA-256 и результаты.
4. Для следующего релиза, чтобы получить delta-пакет, предварительно скачать предыдущие пакеты в `dist/releases` средствами `dotnet vpk download github` или собрать полный пакет без delta.
5. Создать GitHub Release в `rerit33/PeerFerry`, приложить установщик, portable, `*-full.nupkg`, delta (если есть), `releases.win.json`, `assets.win.json`, `RELEASES`, `SHA256SUMS.txt`. Не загружать настройки/профили и `.work`.
6. Проверить публикацию feed и обновление предыдущей установленной версии. Приложение выбирает обычные опубликованные релизы; draft/prerelease не попадут в стабильный канал.

Velopack CLI и библиотека должны иметь одинаковую версию (сейчас 1.2.161). Сборки self-contained Windows x64. Для широкой публичной раздачи отдельно настроить Authenticode-подпись и защиту аккаунта GitHub. Встроенный GitHub access token не используется и не нужен для публичных релизов.
