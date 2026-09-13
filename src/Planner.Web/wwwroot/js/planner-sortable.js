window.plannerSortable = {
    createBoard(element, dotNet, boardId) {
        const instances = [];
        try {
            element.querySelectorAll("[data-column-id]").forEach(column => {
                instances.push(new Sortable(column, {
                    group: `planner-${boardId}`,
                    animation: 150,
                    draggable: ".kanban-card",
                    onEnd: async event => {
                        try {
                            const destinationColumnId = event.to.dataset.columnId;
                            const accepted = await dotNet.invokeMethodAsync("OnCardMoved", event.item.dataset.taskId, destinationColumnId, event.newDraggableIndex ?? event.newIndex);
                            if (!accepted) await dotNet.invokeMethodAsync("OnBoardDragFailed", "Move was rejected; the board was refreshed.");
                        } catch (error) {
                            console.error("Planner board move failed", error);
                            await dotNet.invokeMethodAsync("OnBoardDragFailed", "Move failed; the board was refreshed.");
                        }
                    }
                }));
            });
            return { dispose: () => instances.forEach(instance => instance.destroy()) };
        } catch (error) {
            instances.forEach(instance => instance.destroy());
            console.error("Planner SortableJS initialization failed", error);
            throw error;
        }
    }
};
