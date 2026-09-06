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


function addTranscript(
    speaker,
    text) {

    const message =
        document.createElement("div");

    message.className =
        "message " + speaker;

    message.textContent =
        text;

    conversationElement.appendChild(
        message);

    conversationElement.scrollTop =
        conversationElement.scrollHeight;
}


function handleMessage(message) {

    if (message.type === "state") {

        stateElement.textContent =
            message.value;

        return;
    }


    if (message.type === "status") {

        statusElement.textContent =
            message.value;

        return;
    }


    if (message.type === "transcript") {

        addTranscript(
            message.speaker,
            message.text);

        return;
    }


    if (message.type === "agent") {

        if (message.eventName === "started") {

            startButton.disabled = true;
            stopButton.disabled = false;

        }

        if (message.eventName === "stopped") {

            startButton.disabled = false;
            stopButton.disabled = true;

        }

        return;
    }


    if (message.type === "error") {

        errorElement.textContent =
            message.message;

        return;
    }
}


if (window.chrome &&
    window.chrome.webview) {

    window.chrome.webview.addEventListener(
        "message",
        function (event) {

            try {

                const message =
                    JSON.parse(event.data);

                handleMessage(message);

            }
            catch (error) {

                console.error(error);

            }

        });
}