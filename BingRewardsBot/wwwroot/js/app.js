(function () {
    "use strict";

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/bingRewardsHub")
        .withAutomaticReconnect()
        .build();

    const els = {
        form: document.getElementById("botForm"),
        email: document.getElementById("email"),
        password: document.getElementById("password"),
        desktopCount: document.getElementById("desktopCount"),
        mobileCount: document.getElementById("mobileCount"),
        headless: document.getElementById("headless"),
        btnStart: document.getElementById("btnStart"),
        btnStop: document.getElementById("btnStop"),
        statusDot: document.getElementById("statusDot"),
        statusText: document.getElementById("statusText"),
        desktopBar: document.getElementById("desktopBar"),
        mobileBar: document.getElementById("mobileBar"),
        desktopLabel: document.getElementById("desktopLabel"),
        mobileLabel: document.getElementById("mobileLabel"),
        desktopDone: document.getElementById("desktopDone"),
        mobileDone: document.getElementById("mobileDone"),
        phaseDisplay: document.getElementById("phaseDisplay"),
        logConsole: document.getElementById("logConsole"),
        autoScroll: document.getElementById("autoScroll"),
        btnClearLogs: document.getElementById("btnClearLogs"),
        toastContainer: document.getElementById("toastContainer"),
    };

    let isRunning = false;

    function pct(done, total) {
        if (!total) return 0;
        return Math.min(100, Math.round((done / total) * 100));
    }

    function setRunning(running) {
        isRunning = running;
        els.btnStart.disabled = running;
        els.btnStop.disabled = !running;
        els.email.disabled = running;
        els.password.disabled = running;
        els.desktopCount.disabled = running;
        els.mobileCount.disabled = running;
        els.headless.disabled = running;
    }

    function setStatus(state, text) {
        els.statusDot.className = "status-dot " + state;
        els.statusText.textContent = text;
    }

    function updateProgress(p) {
        if (!p) return;
        const dDone = p.desktop_done || 0;
        const dTotal = p.desktop_total || 0;
        const mDone = p.mobile_done || 0;
        const mTotal = p.mobile_total || 0;

        els.desktopBar.style.width = pct(dDone, dTotal) + "%";
        els.mobileBar.style.width = pct(mDone, mTotal) + "%";
        els.desktopLabel.textContent = dDone + " / " + dTotal;
        els.mobileLabel.textContent = mDone + " / " + mTotal;
        els.desktopDone.textContent = dDone;
        els.mobileDone.textContent = mDone;
        els.phaseDisplay.textContent = p.phase || "—";
    }

    function appendLog(entry) {
        const line = document.createElement("div");
        line.className = "log-line " + (entry.level || "info");
        const ts = document.createElement("span");
        ts.className = "ts";
        ts.textContent = entry.timestamp || "";
        const lvl = document.createElement("span");
        lvl.className = "lvl";
        lvl.textContent = "[" + (entry.level || "info").toUpperCase() + "] ";
        const msg = document.createTextNode(entry.message || "");
        line.appendChild(ts);
        line.appendChild(lvl);
        line.appendChild(msg);
        els.logConsole.appendChild(line);
        if (els.autoScroll.checked) {
            els.logConsole.scrollTop = els.logConsole.scrollHeight;
        }
    }

    function showToast(message, type) {
        type = type || "info";
        const toast = document.createElement("div");
        toast.className = "toast " + type;
        toast.textContent = message;
        els.toastContainer.appendChild(toast);
        setTimeout(function () { toast.remove(); }, 4500);
    }

    function handleStatus(data) {
        const running = data.running;
        setRunning(running);
        updateProgress(data.progress);

        if (running) {
            const phase = (data.progress && data.progress.phase) || "running";
            setStatus("running", "Running: " + phase);
        } else if (data.last_error) {
            setStatus("error", "Error");
        } else {
            const st = (data.progress && data.progress.status) || "idle";
            if (st === "completed") {
                setStatus("idle", "Completed");
                showToast("Bot finished successfully.", "success");
            } else if (st === "stopped") {
                setStatus("idle", "Stopped");
                showToast("Bot stopped.", "warning");
            } else {
                setStatus("idle", "Idle");
            }
        }
    }

    connection.on("log", appendLog);
    connection.on("progress", updateProgress);
    connection.on("status", handleStatus);

    connection.onreconnecting(function () {
        appendLog({ level: "warning", message: "Reconnecting to server...", timestamp: new Date().toISOString() });
    });

    connection.onreconnected(function () {
        appendLog({ level: "info", message: "Reconnected to live log stream.", timestamp: new Date().toISOString() });
        fetch("/api/status").then(function (r) { return r.json(); }).then(handleStatus).catch(function () {});
    });

    connection.onclose(function () {
        appendLog({ level: "warning", message: "Disconnected from server.", timestamp: new Date().toISOString() });
    });

    connection.start()
        .then(function () {
            appendLog({ level: "info", message: "Connected to live log stream.", timestamp: new Date().toISOString() });
        })
        .catch(function (err) {
            appendLog({ level: "error", message: "SignalR connection failed: " + err, timestamp: new Date().toISOString() });
        });

    els.form.addEventListener("submit", function (e) {
        e.preventDefault();
        if (isRunning) return;

        const payload = {
            email: els.email.value.trim(),
            password: els.password.value,
            desktop_count: parseInt(els.desktopCount.value, 10) || 0,
            mobile_count: parseInt(els.mobileCount.value, 10) || 0,
            headless: els.headless.checked,
        };

        fetch("/api/start", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
        })
            .then(function (r) {
                return r.json().then(function (body) { return { ok: r.ok, body: body }; });
            })
            .then(function (res) {
                if (!res.ok) { showToast(res.body.error || "Failed to start.", "error"); return; }
                setRunning(true);
                setStatus("running", "Starting...");
                showToast("Bot started.", "success");
            })
            .catch(function (err) { showToast("Network error: " + err.message, "error"); });
    });

    els.btnStop.addEventListener("click", function () {
        fetch("/api/stop", { method: "POST" })
            .then(function (r) {
                return r.json().then(function (body) { return { ok: r.ok, body: body }; });
            })
            .then(function (res) {
                if (!res.ok) { showToast(res.body.error || "Stop failed.", "error"); return; }
                setStatus("stopping", "Stopping...");
                showToast("Stop signal sent.", "warning");
            })
            .catch(function (err) { showToast("Network error: " + err.message, "error"); });
    });

    els.btnClearLogs.addEventListener("click", function () {
        els.logConsole.innerHTML = "";
    });

    setInterval(function () {
        fetch("/api/status").then(function (r) { return r.json(); }).then(handleStatus).catch(function () {});
    }, 5000);

    fetch("/api/status").then(function (r) { return r.json(); }).then(handleStatus).catch(function () {});
})();
