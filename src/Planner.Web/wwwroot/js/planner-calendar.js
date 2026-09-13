window.plannerCalendar = {
    create(element, dotNet, options) {
        const calendar = new FullCalendar.Calendar(element, {
            initialView: options.initialView,
            firstDay: 1,
            nowIndicator: true,
            slotDuration: "00:30:00",
            snapDuration: "00:30:00",
            slotLabelInterval: "01:00:00",
            slotMinTime: "06:00:00",
            slotMaxTime: "21:00:00",
            scrollTime: "07:00:00",
            allDaySlot: false,
            theme: "theme-classic",
            selectable: options.selectable,
            editable: options.editable,
            eventStartEditable: options.editable,
            eventDurationEditable: options.editable,
            height: "auto",
            headerToolbar: {
                left: "prev,next today",
                center: "title",
                right: options.initialView === "timeGridWeek" ? "timeGridWeek,dayGridMonth" : "dayGridMonth,timeGridWeek"
            },
            buttonText: {
                today: "Today",
                week: "Week",
                month: "Month"
            },
            datesSet(info) {
                dotNet.invokeMethodAsync("OnCalendarRange", info.start.toISOString(), info.end.toISOString());
            },
            select(info) {
                dotNet.invokeMethodAsync("OnCalendarSelect", info.start.toISOString(), info.end.toISOString());
            },
            eventClick(info) {
                const props = info.event.extendedProps;
                dotNet.invokeMethodAsync("OnCalendarEventClick", props.timeBlockId || null, props.taskId || null);
            },
            async eventDrop(info) {
                if (info.event.extendedProps.locked || !await saveChange(dotNet, info.event)) info.revert();
            },
            async eventResize(info) {
                if (info.event.extendedProps.locked || !await saveChange(dotNet, info.event)) info.revert();
            },
            eventContent(info) {
                const time = info.timeText ? `<div class="fc-event-time" style="opacity:.8;font-size:.75em;line-height:1.3">${info.timeText}</div>` : "";
                return {
                    html: `<div style="white-space:normal;word-break:break-word;overflow:visible;line-height:1.2;font-size:.85rem">${time}<div class="fc-event-title">${info.event.title}</div></div>`
                };
            },
            events: [...(options.events || []), ...(options.externalEvents || []).map(event => ({ ...event, editable: false, extendedProps: { ...(event.extendedProps || {}), locked: true } }))]
        });
        calendar.render();
        return {
            update(events, externalEvents) {
                calendar.removeAllEvents();
                [...(events || []), ...(externalEvents || []).map(event => ({ ...event, editable: false, extendedProps: { ...(event.extendedProps || {}), locked: true } }))]
                    .forEach(event => calendar.addEvent(event));
            },
            dispose() {
                calendar.destroy();
            }
        };
    }
};

async function saveChange(dotNet, event) {
    if (!event.start || !event.end) return false;
    return await dotNet.invokeMethodAsync("OnCalendarEventChange", event.extendedProps.timeBlockId, event.start.toISOString(), event.end.toISOString());
}
