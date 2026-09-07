const stateElement =
    document.getElementById("state");

const statusElement =
    document.getElementById("status");

const conversationElement =
    document.getElementById("conversation");

const errorElement =
    document.getElementById("error");

const startButton =
    document.getElementById("startButton");

const stopButton =
    document.getElementById("stopButton");


let currentAgentElement = null;


/* =========================
   COMMANDS
   ========================= */

function sendCommand(command) {

    const message = {
        type: "command",
        command: command
    };

    window.chrome.webview.postMessage(
        JSON.stringify(message)
    );
}


startButton.addEventListener(
    "click",
    function () {
        sendCommand("start");
    });


stopButton.addEventListener(
    "click",
    function () {
        sendCommand("stop");
    });


/* =========================
   SCROLL
   ========================= */

function scrollConversation() {

    conversationElement.scrollTop =
        conversationElement.scrollHeight;
}


/* =========================
   USER MESSAGE
   ========================= */

function addUserMessage(text) {

    /*
     * Every You message starts a new turn.
     */
    currentAgentElement = null;


    const element =
        document.createElement("div");

    element.className =
        "message user";


    element.textContent =
        text.trim();


    /*
     * Blank space BEFORE the next turn.
     */
    element.style.marginTop =
        "16px";


    conversationElement.appendChild(
        element);


    scrollConversation();
}


/* =========================
   AGENT MESSAGE
   ========================= */

function handleAgentMessage(text) {

    if (!text)
        return;


    /*
     * First Agent chunk.
     *
     * Controller sends:
     *
     * Agent: Two plus two equals
     */
    if (!currentAgentElement) {

        const element =
            document.createElement("div");

        element.className =
            "message agent";


        /*
         * No extra gap between
         * You and Agent.
         */
        element.style.marginTop =
            "0";


        element.textContent =
            text;


        conversationElement.appendChild(
            element);


        currentAgentElement =
            element;


        scrollConversation();

        return;
    }


    /*
     * Subsequent streaming chunks.
     */
    currentAgentElement.textContent +=
        text;


    scrollConversation();
}


/* =========================
   TRANSCRIPT
   ========================= */

function handleTranscript(
    speaker,
    text) {

    if (!text)
        return;


    if (speaker === "user") {

        addUserMessage(
            text);

        return;
    }


    if (speaker === "agent") {

        handleAgentMessage(
            text);

        return;
    }
}


/* =========================
   WEBVIEW MESSAGE
   ========================= */

function handleMessage(message) {

    if (!message)
        return;


    /* =====================
       STATE
       ===================== */

    if (message.type === "state") {

        stateElement.textContent =
            message.value || "";

        return;
    }


    /* =====================
       STATUS
       ===================== */

    if (message.type === "status") {

        statusElement.textContent =
            message.value || "";

        return;
    }


    /* =====================
       TRANSCRIPT
       ===================== */

    if (message.type === "transcript") {

        handleTranscript(
            message.speaker,
            message.text);

        return;
    }


    /* =====================
       AGENT
       ===================== */

    if (message.type === "agent") {

        if (message.eventName === "started") {

            startButton.disabled =
                true;

            stopButton.disabled =
                false;

            currentAgentElement =
                null;

            return;
        }


        if (message.eventName === "stopped") {

            startButton.disabled =
                false;

            stopButton.disabled =
                true;

            currentAgentElement =
                null;

            return;
        }
    }


    /* =====================
       ERROR
       ===================== */

    if (message.type === "error") {

        errorElement.textContent =
            message.message || "";

        return;
    }
}


/* =========================
   WEBVIEW2
   ========================= */

if (window.chrome &&
    window.chrome.webview) {

    window.chrome.webview.addEventListener(
        "message",
        function (event) {

            try {

                const message =
                    JSON.parse(event.data);

                handleMessage(
                    message);

            }
            catch (error) {

                console.error(
                    "WebView message error:",
                    error);
            }
        });
}