import TodoCommentsCellRenderer from '@/views/todo/components/TodoCommentsCellRenderer.vue'
import ResponsiblesCellRenderer from '@/views/todo/components/ResponsiblesCellRenderer.vue'

// Each function returns a column-builder lambda for use with CoarGridBuilder.columns([...])

export function criticalColumn() {
  return (col: any) => col.icon('Critical', { color: '#bd0505', size: 's' })
    .option('valueGetter', (p: any) => p.data?.Critical ? 'triangle-alert' : '')
    .fixedWidth(30)
}

export function awaitingFeedbackColumn() {
  return (col: any) => col.icon('AwaitingFeedback', { color: 'darkblue', size: 's' })
    .option('valueGetter', (p: any) => p.data?.AwaitingFeedback ? 'circle-help' : '')
    .fixedWidth(30)
}

export function titleColumn(options?: { rowDrag?: boolean; sortable?: boolean; dueDateWarning?: boolean }) {
  return (col: any) => {
    let c = col.tree('Title').header('Task', 'todo.task').flex(1)
    if (options?.rowDrag) c = c.rowDrag()
    if (options?.sortable) c = c.sortable()
    c = c.option('cellClass', (p: any) => {
      const classes: string[] = []
      if (!p.data?.ParentTodoId) classes.push('todo-title-parent')
      if (options?.dueDateWarning && p.data?.DueDate) {
        const warningDate = new Date()
        warningDate.setDate(warningDate.getDate() + 3)
        if (new Date(p.data.DueDate) < warningDate) {
          classes.push('todo-title-critical')
        }
      }
      return classes.join(' ')
    })
    return c
  }
}

export function descriptionColumn(tooltipComponent?: any) {
  return (col: any) => {
    let c = col.field('Description').header('Description', 'common.description').flex(1)
      .valueFormatter((p: any) => {
        const html = String(p.value ?? '')
        return html.replace(/<[^>]*>/g, '').replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').trim()
      })
      .option('tooltipValueGetter', (p: any) => p.value)
    if (tooltipComponent) {
      c = c.option('tooltipComponent', tooltipComponent)
    }
    return c
  }
}

export function statusColumn() {
  return (col: any) => col.tag('Status', {
    variantMap: {
      Done: 'success',
      InProgress: 'info',
      Info: 'warning',
      New: 'neutral',
    },
    i18nPrefix: 'todo.status.',
  }).header('Status', 'common.status').width(120)
    .option('valueGetter', (p: any) => p.data?.Status === 'None' ? null : p.data?.Status)
}

export function commentsColumn(options?: { quickFilter?: boolean }) {
  return (col: any) => {
    let c = col.field('CommentsCount').header('').fixedWidth(120)
      .cellRenderer(TodoCommentsCellRenderer)
    if (options?.quickFilter === false) c = c.quickFilter(false)
    return c
  }
}

export function dueDateColumn(options?: { warnDays?: number }) {
  return (col: any) => {
    let c = col.date('DueDate').header('Due Date', 'todo.dueDate').fixedWidth(150)
    if (options?.warnDays) {
      c = c.option('cellClass', (p: any) => {
        if (!p.data?.DueDate) return ''
        const warningDate = new Date()
        warningDate.setDate(warningDate.getDate() + options.warnDays!)
        if (new Date(p.data.DueDate) < warningDate) {
          return p.data.ParentTodoId ? 'todo-due-warning' : 'todo-due-warning todo-due-warning-bold'
        }
        return ''
      })
    }
    return c
  }
}

export function responsiblesColumn(options?: { quickFilter?: boolean }) {
  return (col: any) => {
    let c = col.field('Responsibles').header('Responsible', 'todo.responsible').width(120)
      .cellRenderer(ResponsiblesCellRenderer)
    if (options?.quickFilter) {
      c = c.quickFilter((_val: any, data: any) => data.Responsibles?.map((r: any) => r.Label).join(' ') ?? '')
    }
    return c
  }
}

export function customerColumn(options?: { quickFilter?: boolean }) {
  return (col: any) => {
    let c = col.field('Customer').header('Customer', 'todo.customer').fixedWidth(150)
      .valueFormatter((p: any) => (p.value as any)?.Label ?? '')
    if (options?.quickFilter) {
      c = c.quickFilter((_val: any, data: any) => data.Customer?.Label ?? '')
    }
    return c
  }
}

export function createdByColumn() {
  return (col: any) => col.field('CreatedBy').header('Created By', 'todo.createdBy').fixedWidth(100)
    .cellRenderer(ResponsiblesCellRenderer)
    .quickFilter((_val: any, data: any) => data.CreatedBy?.Label ?? '')
}

export function lastModifiedColumn() {
  return (col: any) => col.date('LastTouchedAt', { includeTime: true })
    .header('Last Modified', 'todo.lastModified').width(160).sortable()
}
