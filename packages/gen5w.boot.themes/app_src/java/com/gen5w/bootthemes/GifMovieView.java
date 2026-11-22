package com.gen5w.bootthemes;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Movie;
import android.os.SystemClock;
import android.view.View;

/**
 * Hardware-friendly Android 4.4.2 animated GIF player view backed by `android.graphics.Movie`.
 * Uses software layer rendering so `Movie.draw(Canvas)` works reliably on KitKat HWUI.
 */
public final class GifMovieView extends View {
    private Movie movie;
    private long movieStartMs = 0L;
    private boolean playing = true;

    public GifMovieView(Context context) {
        super(context);
        setLayerType(View.LAYER_TYPE_SOFTWARE, null);
        setBackgroundColor(Color.BLACK);
    }

    public void setGifBytes(byte[] gifBytes) {
        if (gifBytes == null || gifBytes.length == 0) {
            this.movie = null;
        } else {
            this.movie = Movie.decodeByteArray(gifBytes, 0, gifBytes.length);
        }
        this.movieStartMs = 0L;
        invalidate();
    }

    public void setPlaying(boolean playing) {
        this.playing = playing;
        if (playing) {
            invalidate();
        }
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (movie == null) return;
        int mw = movie.width();
        int mh = movie.height();
        if (mw <= 0 || mh <= 0) return;

        long now = SystemClock.uptimeMillis();
        if (movieStartMs == 0L) {
            movieStartMs = now;
        }
        int dur = movie.duration();
        if (dur <= 0) dur = 3000;
        int relTime = (int) ((now - movieStartMs) % dur);
        movie.setTime(relTime);

        int vw = getWidth();
        int vh = getHeight();
        if (vw > 0 && vh > 0) {
            float sx = (float) vw / (float) mw;
            float sy = (float) vh / (float) mh;
            float s = Math.min(sx, sy);
            float dx = (vw - mw * s) * 0.5f;
            float dy = (vh - mh * s) * 0.5f;
            canvas.save();
            canvas.translate(dx, dy);
            canvas.scale(s, s);
            movie.draw(canvas, 0, 0);
            canvas.restore();
        }

        if (playing && isShown()) {
            postInvalidateDelayed(45L);
        }
    }
}
