window.prepTubeNotifications = {
    enable: async function () {
        if (!('Notification' in window)) return;
        await Notification.requestPermission();
    },
    show: async function (title, body) {
        if (!('Notification' in window)) return;
        if (Notification.permission === 'default') {
            await Notification.requestPermission();
        }
        if (Notification.permission === 'granted') {
            new Notification(title, { body: body, icon: '/favicon.png', tag: 'preptube-interest' });
        }
    }
};
