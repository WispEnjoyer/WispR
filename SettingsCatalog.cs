using System;
using System.Collections.Generic;
using System.Linq;

namespace WispR
{
    /// <summary>Windows settings pages (and a few classic tools) the launcher can jump straight to.</summary>
    static class SettingsCatalog
    {
        public sealed class Page
        {
            public string Name;      // shown in the launcher
            public string Target;    // ms-settings: URI or program
            public string Args;
            public AppEntry[] Terms; // name + search words (English and German), for matching
        }

        static Page P(string name, string target, string words, string args = null) => new Page
        {
            Name = name,
            Target = target,
            Args = args,
            Terms = new[] { name }.Concat(words.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()))
                .Where(w => w.Length > 0).Select(w => new AppEntry(w)).ToArray(),
        };

        public static readonly Page[] Pages =
        {
            // System
            P("Display settings", "ms-settings:display", "display, screen, monitor, resolution, brightness, scale, scaling, hdr, refresh rate, anzeige, bildschirm, auflösung, helligkeit, skalierung, bildwiederholrate"),
            P("Night light", "ms-settings:nightlight", "night light, blue light, nachtmodus, blaulicht"),
            P("Graphics settings", "ms-settings:display-advancedgraphics", "graphics, gpu, graphics card, grafik, grafikkarte"),
            P("Sound settings", "ms-settings:sound", "sound, audio, volume, speaker, speakers, headphones, microphone, mic, output device, input device, lautstärke, lautsprecher, kopfhörer, mikrofon, ton"),
            P("Sound control panel", "mmsys.cpl", "playback devices, recording devices, sound devices, wiedergabegeräte, aufnahmegeräte"),
            P("Notifications", "ms-settings:notifications", "notifications, benachrichtigungen, mitteilungen"),
            P("Do not disturb / Focus", "ms-settings:quiethours", "do not disturb, focus, focus assist, quiet hours, nicht stören, fokus"),
            P("Power & battery", "ms-settings:powersleep", "power, battery, sleep, screen timeout, energy, power mode, energie, akku, ruhezustand, energiesparmodus, bildschirm aus"),
            P("Power options", "powercfg.cpl", "power plan, energy plan, energieoptionen, energiesparplan, höchstleistung"),
            P("Storage", "ms-settings:storagesense", "storage, disk space, storage sense, cleanup, speicher, speicherplatz, festplatte, speicheroptimierung"),
            P("Multitasking", "ms-settings:multitasking", "multitasking, snap, snap layouts, virtual desktops, alt tab, fenster andocken, virtuelle desktops"),
            P("Clipboard", "ms-settings:clipboard", "clipboard, clipboard history, zwischenablage"),
            P("Remote desktop", "ms-settings:remotedesktop", "remote desktop, rdp, remotedesktop"),
            P("About this PC", "ms-settings:about", "about, pc name, rename pc, system info, specs, specifications, windows version, info, gerätename, pc umbenennen, spezifikationen"),
            P("Activation", "ms-settings:activation", "activation, product key, license, aktivierung, produktschlüssel, lizenz"),
            P("Troubleshoot", "ms-settings:troubleshoot", "troubleshoot, troubleshooter, fix problems, problembehandlung"),
            P("Recovery", "ms-settings:recovery", "recovery, reset pc, reset, advanced startup, wiederherstellung, zurücksetzen, pc zurücksetzen"),
            P("For developers", "ms-settings:developers", "developer, developer mode, entwickler, entwicklermodus"),
            // Devices
            P("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth, devices, pair, pairing, headset, controller, geräte, koppeln"),
            P("Printers & scanners", "ms-settings:printers", "printer, printers, scanner, print, drucker, drucken"),
            P("Mouse", "ms-settings:mousetouchpad", "mouse, cursor speed, pointer, scroll, maus, mauszeiger, zeigergeschwindigkeit"),
            P("Touchpad", "ms-settings:devices-touchpad", "touchpad, trackpad, gestures, gesten"),
            P("Typing", "ms-settings:typing", "typing, autocorrect, spell check, text suggestions, eingabe, autokorrektur, rechtschreibung"),
            P("AutoPlay", "ms-settings:autoplay", "autoplay, automatische wiedergabe"),
            P("USB", "ms-settings:usb", "usb"),
            P("Device Manager", "devmgmt.msc", "device manager, drivers, driver, geräte-manager, gerätemanager, treiber"),
            // Network
            P("Wi-Fi", "ms-settings:network-wifi", "wifi, wi-fi, wlan, wireless, drahtlos"),
            P("Network & internet", "ms-settings:network-status", "network, internet, connection, netzwerk, verbindung"),
            P("Ethernet", "ms-settings:network-ethernet", "ethernet, lan, cable, kabel"),
            P("VPN", "ms-settings:network-vpn", "vpn"),
            P("Proxy", "ms-settings:network-proxy", "proxy"),
            P("Airplane mode", "ms-settings:network-airplanemode", "airplane mode, flight mode, flugmodus, flugzeugmodus"),
            P("Mobile hotspot", "ms-settings:network-mobilehotspot", "hotspot, mobile hotspot, tethering"),
            P("Network connections", "ncpa.cpl", "network adapters, adapter settings, netzwerkverbindungen, adapteroptionen"),
            // Personalisation
            P("Background", "ms-settings:personalization-background", "background, wallpaper, desktop background, hintergrund, hintergrundbild"),
            P("Colors", "ms-settings:colors", "colors, colours, dark mode, light mode, accent color, transparency, farben, dunkelmodus, dunkler modus, akzentfarbe"),
            P("Themes", "ms-settings:themes", "themes, desktop icons, cursor, designs, desktopsymbole"),
            P("Lock screen", "ms-settings:lockscreen", "lock screen, sperrbildschirm"),
            P("Fonts", "ms-settings:fonts", "fonts, schriftarten"),
            P("Start settings", "ms-settings:personalization-start", "start menu, start, startmenü"),
            P("Taskbar settings", "ms-settings:taskbar", "taskbar, taskleiste"),
            // Apps
            P("Installed apps", "ms-settings:appsfeatures", "apps, installed apps, uninstall, remove program, programs, apps & features, installierte apps, deinstallieren, programme, entfernen"),
            P("Programs and Features", "appwiz.cpl", "programs and features, uninstall a program, programme und features"),
            P("Default apps", "ms-settings:defaultapps", "default apps, default browser, file associations, open with, standard-apps, standardbrowser, standardprogramme"),
            P("Startup apps", "ms-settings:startupapps", "startup, startup apps, autostart, start with windows, beim start"),
            P("Optional features", "ms-settings:optionalfeatures", "optional features, optionale features"),
            // Accounts
            P("Your info", "ms-settings:yourinfo", "account, your info, profile picture, konto, ihre infos, profilbild"),
            P("Sign-in options", "ms-settings:signinoptions", "sign-in, password, pin, windows hello, fingerprint, face, anmeldeoptionen, passwort, kennwort, fingerabdruck"),
            P("Email & accounts", "ms-settings:emailandaccounts", "email, accounts, e-mail, konten"),
            P("Family & other users", "ms-settings:otherusers", "other users, add user, family, benutzer, familie, andere benutzer"),
            // Time & language
            P("Date & time", "ms-settings:dateandtime", "date, time, clock, time zone, sync time, datum, uhrzeit, zeitzone, uhr"),
            P("Language & region", "ms-settings:regionlanguage", "language, display language, region, format, sprache, anzeigesprache, regionalformat"),
            P("Keyboard layouts", "ms-settings:keyboard", "keyboard, keyboard layout, input language, tastatur, tastaturlayout, eingabesprache"),
            P("Speech", "ms-settings:speech", "speech, voice, spracherkennung, stimme"),
            // Gaming
            P("Game Mode", "ms-settings:gaming-gamemode", "game mode, gaming, spielmodus, spiele"),
            P("Xbox Game Bar", "ms-settings:gaming-gamebar", "game bar, xbox"),
            P("Captures", "ms-settings:gaming-gamedvr", "captures, game clips, screen recording, aufnahmen, bildschirmaufnahme"),
            // Accessibility
            P("Accessibility", "ms-settings:easeofaccess", "accessibility, ease of access, barrierefreiheit, erleichterte bedienung"),
            P("Text size", "ms-settings:easeofaccess-display", "text size, bigger text, textgröße, schriftgröße"),
            P("Narrator", "ms-settings:easeofaccess-narrator", "narrator, screen reader, sprachausgabe"),
            P("Magnifier", "ms-settings:easeofaccess-magnifier", "magnifier, zoom, bildschirmlupe, lupe"),
            // Privacy & security
            P("Privacy & security", "ms-settings:privacy", "privacy, permissions, datenschutz, berechtigungen"),
            P("Camera permissions", "ms-settings:privacy-webcam", "camera, webcam, kamera"),
            P("Microphone permissions", "ms-settings:privacy-microphone", "microphone permission, mikrofonzugriff"),
            P("Location", "ms-settings:privacy-location", "location, gps, standort"),
            P("Windows Security", "windowsdefender:", "windows security, defender, antivirus, virus, firewall, windows-sicherheit, virenschutz"),
            P("Windows Update", "ms-settings:windowsupdate", "update, updates, windows update, aktualisierung, aktualisieren"),
            P("Backup", "ms-settings:backup", "backup, onedrive backup, sicherung"),
            // Classic tools
            P("Control Panel", "control.exe", "control panel, systemsteuerung"),
            P("Services", "services.msc", "services, dienste"),
            P("Disk Management", "diskmgmt.msc", "disk management, partitions, format drive, datenträgerverwaltung, partition"),
            P("Environment variables", "rundll32.exe", "environment variables, path, umgebungsvariablen", "sysdm.cpl,EditEnvironmentVariables"),
            P("System properties", "sysdm.cpl", "system properties, advanced system settings, virtual memory, pagefile, systemeigenschaften, erweiterte systemeinstellungen, auslagerungsdatei"),
            P("Registry Editor", "regedit.exe", "registry, regedit, registrierungs-editor, registrierung"),
            P("Event Viewer", "eventvwr.msc", "event viewer, logs, ereignisanzeige"),
            P("Task Scheduler", "taskschd.msc", "task scheduler, aufgabenplanung"),
            P("Resource Monitor", "resmon.exe", "resource monitor, ressourcenmonitor"),
        };

        /// <summary>Settings pages matching the query, with a score comparable to app scores.</summary>
        public static IEnumerable<(Page page, int score)> Search(string q)
        {
            if (q.Length < 2) yield break;
            foreach (var p in Pages)
            {
                int best = -1;
                for (int i = 0; i < p.Terms.Length; i++)
                {
                    int sc = Matcher.Score(q, p.Terms[i]);
                    if (sc > best) best = sc;
                }
                // Only confident matches (start of a word or better) — settings shouldn't clutter app searches.
                if (best >= 600) yield return (p, best);
            }
        }
    }
}
