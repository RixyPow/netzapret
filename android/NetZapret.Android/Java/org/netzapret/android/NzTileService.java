package org.netzapret.android;

import android.app.PendingIntent;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.VpnService;
import android.os.Build;
import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;

/**
 * Плитка в шторке (ui.md: «Главная» — кнопка, уведомление и плитка):
 * включить с последним запросом приложения или выключить. Запустить без
 * приложения можно, только если разрешение на VPN уже выдано и запрос
 * сохранён; иначе плитка открывает приложение — окно разрешения
 * показывает только активность.
 */
public final class NzTileService extends TileService {
    @Override
    public void onStartListening() {
        super.onStartListening();
        refresh();
    }

    @Override
    public void onClick() {
        super.onClick();
        try {
            NzBox.remember(this);
            String state = NzBox.state();
            if (NzBox.RUNNING.equals(state) || NzBox.STARTING.equals(state)) {
                NzBox.stop(this);
            } else {
                boolean prepared;
                try {
                    prepared = VpnService.prepare(this) == null;
                } catch (Throwable t) {
                    prepared = false;
                }
                if (!prepared || !NzRequest.saved(this)) {
                    openApp();
                    return;
                }
                NzBox.startSaved(this);
            }
        } catch (Throwable t) {
            NzBox.LOG.add(NzLog.NZ, "плитка: " + NzSecrets.describe(t));
        }
        refresh();
    }

    private void refresh() {
        Tile tile = getQsTile();
        if (tile == null) {
            return;
        }
        String state = NzBox.state();
        boolean on = NzBox.RUNNING.equals(state) || NzBox.STARTING.equals(state);
        tile.setState(on ? Tile.STATE_ACTIVE : Tile.STATE_INACTIVE);
        tile.setLabel("NetZapret");
        if (Build.VERSION.SDK_INT >= 29) {
            tile.setSubtitle(subtitle(state));
        }
        tile.updateTile();
    }

    private static String subtitle(String state) {
        if (NzBox.RUNNING.equals(state)) {
            return "Работает";
        }
        if (NzBox.STARTING.equals(state)) {
            return "Запускается…";
        }
        if (NzBox.STOPPING.equals(state)) {
            return "Останавливается…";
        }
        if (NzBox.FAILED.equals(state)) {
            return "Ошибка";
        }
        return "Выключен";
    }

    private void openApp() {
        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch == null) {
            return;
        }
        launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        if (Build.VERSION.SDK_INT >= 34) {
            // С Android 14 — только через PendingIntent: открытие активности Intent'ом из плитки запрещено.
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, launch,
                    PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT));
        } else {
            startActivityAndCollapseCompat(launch);
        }
    }

    @SuppressWarnings("deprecation")
    private void startActivityAndCollapseCompat(Intent intent) {
        startActivityAndCollapse(intent);
    }

    /** Попросить систему перечитать плитку (она в шторке и не слушает сейчас). */
    static void requestUpdate(Context context) {
        try {
            TileService.requestListeningState(context, new ComponentName(context, NzTileService.class));
        } catch (Throwable ignored) {
            // плитка не добавлена в шторку или служба плитки не объявлена — не страшно
        }
    }
}
