P3R CO-OP (Persona 3 Reload, two players)
==========================================

What it is
- Two PCs, each with its own Persona 3 Reload (Steam). The host's party is split into two duos:
  the HOST plays the protagonist + the 1st party member, the GUEST plays the 2nd + 3rd members.
- You see each other in Tartarus. Every battle is shared: when one of you touches a Shadow, the other
  game joins the same battle automatically, and each player commands their own duo.
- Bosses (floor guardians) wait until BOTH players have reached the boss and chosen to fight.
  F8 = start the boss without waiting.
- Everything (levels, Personas, items) comes from the HOST's save. The guest gets a copy of it.

Setup (once)
1. Unzip this folder anywhere and double-click Install.bat.
   - It needs the .NET 9 Desktop Runtime; if it is missing, the download page opens - install it and run
     Install.bat again.
   - It finds P3R in Steam (or asks for P3R.exe) and installs everything into %USERPROFILE%\P3R-Coop.
     The game folder is not changed. An existing Reloaded-II setup is backed up.
2. Both PCs must be in the same network: the same LAN/Wi-Fi, or a virtual LAN like Radmin VPN or ZeroTier.
3. When Windows Firewall asks about P3RCoopLink, allow it (private networks).

Playing
- HOST: desktop shortcut "P3R Host co-op". Type the save slot to share (its number in the Load menu).
- GUEST: desktop shortcut "P3R Join co-op". Press Enter (finds the host) or type the host's IP.
  The link window shows "HOST'S SAVE IS IN YOUR SLOT N" - load THAT slot in the game.
- Both: go to Tartarus. Keep the black link window open while playing.
- The partner's characters act by themselves on your screen (their real choice comes over the network).

If something goes wrong
- Logs: %USERPROFILE%\P3R-Coop\recon\coop.log and battle.log - send them to the host.


КООП P3R (Persona 3 Reload, два игрока)
=======================================

Что это
- Два ПК, у каждого своя Persona 3 Reload (Steam). Отряд хоста делится на две пары:
  ХОСТ играет за главного героя + 1-го члена отряда, ГОСТЬ - за 2-го и 3-го.
- Вы видите друг друга в Тартаре. Все бои общие: если один коснулся Тени, игра второго
  автоматически входит в тот же бой, и каждый командует своей парой.
- Боссы (стражи этажей) ждут, пока ОБА игрока дойдут до босса и выберут бой.
  F8 = начать бой с боссом, не дожидаясь напарника.
- Всё (уровни, персоны, предметы) берётся из сохранения ХОСТА. Гость получает его копию.

Установка (один раз)
1. Распакуйте папку куда угодно и запустите Install.bat.
   - Нужен .NET 9 Desktop Runtime; если его нет, откроется страница загрузки - установите
     и запустите Install.bat ещё раз.
   - Установщик сам найдёт P3R в Steam (или спросит P3R.exe) и поставит всё в %USERPROFILE%\P3R-Coop.
     Папка игры не меняется. Старые настройки Reloaded-II сохраняются в резервную копию.
2. Оба ПК должны быть в одной сети: одна LAN/Wi-Fi или виртуальная сеть (Radmin VPN, ZeroTier).
3. Когда брандмауэр Windows спросит про P3RCoopLink - разрешите (частные сети).

Игра
- ХОСТ: ярлык "P3R Host co-op". Введите номер слота сохранения (как в меню загрузки).
- ГОСТЬ: ярлык "P3R Join co-op". Нажмите Enter (найдёт хоста) или введите IP хоста.
  В окне связи появится "HOST'S SAVE IS IN YOUR SLOT N" - загрузите в игре ИМЕННО этот слот.
- Оба: идите в Тартар. Чёрное окно связи не закрывайте.
- Персонажи напарника на вашем экране действуют сами (их настоящий выбор приходит по сети).

Если что-то не так
- Логи: %USERPROFILE%\P3R-Coop\recon\coop.log и battle.log - отправьте их хосту.
