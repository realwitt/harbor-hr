import { parseDate } from '@internationalized/date'
import type { ReactNode } from 'react'
import {
  Button as AriaButton,
  Calendar,
  CalendarCell,
  CalendarGrid,
  CalendarGridBody,
  CalendarGridHeader,
  CalendarHeaderCell,
  CalendarHeading,
  Checkbox as AriaCheckbox,
  DateInput,
  DatePicker,
  DateSegment,
  Dialog,
  FieldError,
  Group,
  Heading,
  Input,
  Label,
  ListBox,
  ListBoxItem,
  Modal,
  ModalOverlay,
  Popover,
  Select,
  SelectValue,
  TextField as AriaTextField,
} from 'react-aria-components'
import { ApiError } from './api'

const fieldClass = 'h-7 rounded-sm border border-neutral-300 bg-white px-2 text-sm outline-none'
const labelClass = 'text-xs text-neutral-600'

export function errorText(error: unknown): string {
  if (!error) {
    return ''
  }

  if (typeof error === 'string') {
    return error
  }

  if (error instanceof ApiError) {
    return error.errors.map((item) => item.message).join(' ')
  }

  if (error instanceof Error) {
    return error.message
  }

  if (Array.isArray(error)) {
    return error.map((item) => errorText(item)).filter(Boolean).join(' ')
  }

  if (typeof error === 'object' && 'message' in error) {
    const message = (error as { message: unknown }).message
    if (typeof message === 'string') {
      return message
    }
  }

  return 'The request failed.'
}

export function ErrorText({ error }: { error: unknown }) {
  const text = errorText(error)
  if (!text) {
    return null
  }

  return <p className="text-sm text-red-700">{text}</p>
}

export function Page({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3">
      <h1 className="text-base font-semibold">{title}</h1>
      {children}
    </section>
  )
}

export function Button({
  children,
  onPress,
  type = 'button',
  isDisabled,
  quiet,
}: {
  children: ReactNode
  onPress?: () => void
  type?: 'button' | 'submit' | 'reset'
  isDisabled?: boolean
  quiet?: boolean
}) {
  return (
    <AriaButton
      type={type}
      onPress={onPress}
      isDisabled={isDisabled}
      className={`inline-flex h-7 items-center rounded-sm px-2 text-sm outline-none data-[disabled]:opacity-50 data-[focus-visible]:outline data-[focus-visible]:outline-2 data-[focus-visible]:outline-neutral-800 ${
        quiet
          ? 'border border-transparent text-neutral-700 data-[hovered]:bg-neutral-100'
          : 'border border-neutral-300 bg-white data-[hovered]:bg-neutral-100 data-[pressed]:bg-neutral-200'
      }`}
    >
      {children}
    </AriaButton>
  )
}

export function TextField({
  label,
  value,
  onChange,
  onBlur,
  type = 'text',
  autoComplete,
  error,
  isReadOnly,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  onBlur?: () => void
  type?: 'text' | 'email' | 'password'
  autoComplete?: string
  error?: string
  isReadOnly?: boolean
}) {
  return (
    <AriaTextField
      value={value}
      onChange={onChange}
      onBlur={onBlur}
      type={type}
      autoComplete={autoComplete}
      isReadOnly={isReadOnly}
      isInvalid={Boolean(error)}
      className="flex min-w-0 flex-col gap-0.5"
    >
      <Label className={labelClass}>{label}</Label>
      <Input className={`${fieldClass} data-[focus-visible]:border-neutral-800`} />
      {error ? <FieldError className="text-xs text-red-700">{error}</FieldError> : null}
    </AriaTextField>
  )
}

export function Choice({
  label,
  value,
  onChange,
  options,
  error,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  options: { id: string; label: string }[]
  error?: string
}) {
  return (
    <Select
      selectedKey={value || null}
      onSelectionChange={(key) => onChange(key == null ? '' : String(key))}
      isInvalid={Boolean(error)}
      className="flex min-w-36 flex-col gap-0.5"
    >
      <Label className={labelClass}>{label}</Label>
      <AriaButton className={`${fieldClass} flex items-center justify-between gap-2 text-left data-[focus-visible]:border-neutral-800`}>
        <SelectValue className="truncate" />
      </AriaButton>
      {error ? <FieldError className="text-xs text-red-700">{error}</FieldError> : null}
      <Popover className="min-w-(--trigger-width) border border-neutral-300 bg-white shadow-sm">
        <ListBox className="max-h-60 overflow-auto p-1 outline-none">
          {options.map((option) => (
            <ListBoxItem
              key={option.id}
              id={option.id}
              className="cursor-pointer rounded-sm px-2 py-1 text-sm outline-none data-[focused]:bg-neutral-100 data-[selected]:font-medium"
            >
              {option.label}
            </ListBoxItem>
          ))}
        </ListBox>
      </Popover>
    </Select>
  )
}

export function DateField({
  label,
  value,
  onChange,
  onFocusDate,
  error,
}: {
  label: string
  value: string
  onChange: (iso: string) => void
  onFocusDate?: (iso: string) => void
  error?: string
}) {
  return (
    <DatePicker
      granularity="day"
      value={value ? parseDate(value) : null}
      onChange={(next) => {
        const iso = next ? next.toString() : ''
        onChange(iso)
        if (iso) {
          onFocusDate?.(iso)
        }
      }}
      isInvalid={Boolean(error)}
      className="flex flex-col gap-0.5"
    >
      <Label className={labelClass}>{label}</Label>
      <Group className={`${fieldClass} flex items-center pr-0`}>
        <DateInput className="flex flex-1 px-0">
          {(segment) => (
            <DateSegment
              segment={segment}
              className="rounded-sm px-0.5 tabular-nums outline-none data-[focused]:bg-neutral-200"
            />
          )}
        </DateInput>
        <AriaButton className="h-7 px-2 text-xs outline-none data-[focus-visible]:outline data-[focus-visible]:outline-2 data-[focus-visible]:outline-neutral-800">
          Open
        </AriaButton>
      </Group>
      {error ? <FieldError className="text-xs text-red-700">{error}</FieldError> : null}
      <Popover className="border border-neutral-300 bg-white p-2 shadow-sm">
        <Calendar className="w-fit">
          <header className="flex items-center gap-1">
            <AriaButton slot="previous" className="h-6 w-6 text-sm">
              ‹
            </AriaButton>
            <CalendarHeading className="flex-1 text-center text-sm" />
            <AriaButton slot="next" className="h-6 w-6 text-sm">
              ›
            </AriaButton>
          </header>
          <CalendarGrid className="border-spacing-0">
            <CalendarGridHeader>
              {(day) => <CalendarHeaderCell className="text-xs text-neutral-500">{day}</CalendarHeaderCell>}
            </CalendarGridHeader>
            <CalendarGridBody>
              {(date) => (
                <CalendarCell
                  date={date}
                  onHoverStart={() => onFocusDate?.(date.toString())}
                  className="flex h-7 w-7 items-center justify-center rounded-sm text-xs outline-none data-[hovered]:bg-neutral-200 data-[selected]:bg-neutral-800 data-[selected]:text-white data-[focus-visible]:outline data-[focus-visible]:outline-2 data-[focus-visible]:outline-neutral-800"
                />
              )}
            </CalendarGridBody>
          </CalendarGrid>
        </Calendar>
      </Popover>
    </DatePicker>
  )
}

export function Check({
  label,
  isSelected,
  onChange,
}: {
  label: string
  isSelected: boolean
  onChange: (value: boolean) => void
}) {
  return (
    <AriaCheckbox isSelected={isSelected} onChange={onChange} className="group flex items-center gap-2 text-sm">
      <span className="h-3.5 w-3.5 border border-neutral-400 bg-white group-data-[selected]:bg-neutral-800" />
      {label}
    </AriaCheckbox>
  )
}

export function Prompt({
  open,
  onOpenChange,
  title,
  children,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  children: ReactNode
}) {
  return (
    <ModalOverlay
      isOpen={open}
      onOpenChange={onOpenChange}
      isDismissable
      className="fixed inset-0 z-20 flex items-start justify-center bg-black/30 p-6"
    >
      <Modal className="w-full max-w-md border border-neutral-300 bg-white shadow-sm outline-none">
        <Dialog className="p-3 outline-none">
          <Heading slot="title" className="text-sm font-semibold">
            {title}
          </Heading>
          <div className="mt-2 flex flex-col gap-2">{children}</div>
        </Dialog>
      </Modal>
    </ModalOverlay>
  )
}

export function fieldErrors(errors: ReadonlyArray<unknown>): string {
  return errors.map((error) => errorText(error)).filter(Boolean).join(' ')
}
