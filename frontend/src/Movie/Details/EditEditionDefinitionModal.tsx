import React, { useCallback, useMemo, useState } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import { inputTypes, kinds } from 'Helpers/Props';
import MovieEdition, { EditionNamingPreview } from 'Movie/MovieEdition';
import postEdition from 'Movie/postEdition';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

export interface DefinitionSelection {
  target: MovieEdition;
  movieMonitored: boolean;
}

interface Attribute {
  key: string;
  value: string;
}

interface EditEditionDefinitionModalProps {
  selection: DefinitionSelection;
  onSaved(): void;
  onError(message: string): void;
  onClose(): void;
}

const columns = [
  { name: 'name', label: () => translate('Edition'), isVisible: true },
  { name: 'path', label: () => translate('File'), isVisible: true },
];

function EditEditionDefinitionModal({
  selection,
  onSaved,
  onError,
  onClose,
}: EditEditionDefinitionModalProps) {
  const [name, setName] = useState(selection.target.name);
  const [attributes, setAttributes] = useState<Attribute[]>(
    Object.entries(selection.target.attributes ?? {}).map(([key, value]) => ({
      key,
      value,
    }))
  );
  const [rules, setRules] = useState(
    (selection.target.matchRules ?? []).join('\n')
  );
  const [preview, setPreview] = useState<EditionNamingPreview>();
  const [previewRequest, setPreviewRequest] = useState<object>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<'preview' | 'save'>();
  const request = useMemo(
    () => ({
      movieId: selection.target.movieId,
      name,
      attributes: Object.fromEntries(attributes.map((a) => [a.key, a.value])),
      matchRules: rules.split('\n').filter((rule) => rule.trim()),
      expectedMovieMonitored: selection.movieMonitored,
      expectedEditionMonitored: selection.target.monitored,
      expectedMovieFileId: selection.target.movieFileId ?? null,
      expectedQualityProfileId: selection.target.qualityProfileId,
      expectedRuleRevision: selection.target.ruleRevision,
      expectedName: selection.target.name,
    }),
    [selection, name, attributes, rules]
  );
  const attributesValid =
    attributes.every((a) => a.key.trim() && a.value.trim()) &&
    new Set(attributes.map((a) => a.key.toLowerCase())).size ===
      attributes.length;
  const previewCurrent = previewRequest === request;
  const invalidate = useCallback(() => {
    setPreview(undefined);
    setPreviewRequest(undefined);
    setError(undefined);
  }, []);
  const onNameChange = useCallback(
    ({ value }: { value: string }) => {
      setName(value);
      invalidate();
    },
    [invalidate]
  );
  const onAttributesChange = useCallback(
    ({ value }: { value: Attribute[] }) => {
      setAttributes(value);
      invalidate();
    },
    [invalidate]
  );
  const onRulesChange = useCallback(
    ({ value }: { value: string }) => {
      setRules(value);
      invalidate();
    },
    [invalidate]
  );
  const onCancel = useCallback(() => {
    if (!busy) onClose();
  }, [busy, onClose]);
  const reportError = useCallback(
    (requestError: unknown) => {
      setPreview(undefined);
      setPreviewRequest(undefined);
      const message =
        requestError instanceof ApiError
          ? getErrorMessage(
              requestError,
              translate('EditionDefinitionFailed')
            ) || translate('EditionDefinitionFailed')
          : translate('EditionDefinitionFailed');
      if (
        requestError instanceof ApiError &&
        (requestError.statusCode === 404 || requestError.statusCode === 409)
      ) {
        onError(message);
      } else {
        setError(message);
      }
    },
    [onError]
  );
  const onPreview = useCallback(async () => {
    setBusy('preview');
    invalidate();
    try {
      const result = await postEdition<EditionNamingPreview>(
        `/movieedition/${selection.target.id}/definition/preview`,
        request
      );
      setPreview(result);
      setPreviewRequest(request);
    } catch (requestError) {
      reportError(requestError);
    } finally {
      setBusy(undefined);
    }
  }, [selection, request, invalidate, reportError]);
  const onSave = useCallback(async () => {
    if (busy || !previewCurrent || !preview?.editingEnabled) return;
    setBusy('save');
    try {
      await postEdition<MovieEdition>(
        `/movieedition/${selection.target.id}/definition`,
        request
      );
      onSaved();
    } catch (requestError) {
      reportError(requestError);
    } finally {
      setBusy(undefined);
    }
  }, [busy, previewCurrent, preview, selection, request, onSaved, reportError]);
  return (
    <Modal isOpen={true} onModalClose={onCancel}>
      <ModalContent onModalClose={onCancel}>
        <ModalHeader>{translate('EditEditionDefinition')}</ModalHeader>
        <ModalBody>
          <Alert>{translate('EditionDefinitionScopeHelpText')}</Alert>
          <fieldset disabled={!!busy}>
            <FormGroup>
              <FormLabel>{translate('Name')}</FormLabel>
              <FormInputGroup
                type={inputTypes.TEXT}
                name="editionDefinitionName"
                value={name}
                onChange={onNameChange}
              />
            </FormGroup>
            <FormGroup>
              <FormLabel>{translate('EditionAttributes')}</FormLabel>
              <FormInputGroup
                type={inputTypes.KEY_VALUE_LIST}
                name="editionDefinitionAttributes"
                value={attributes}
                keyPlaceholder={translate('EditionAttribute')}
                valuePlaceholder={translate('EditionAttributeValue')}
                helpText={translate('EditionAttributesHelpText')}
                onChange={onAttributesChange}
              />
            </FormGroup>
            <FormGroup>
              <FormLabel>{translate('EditionMatchRules')}</FormLabel>
              <FormInputGroup
                type={inputTypes.TEXT_AREA}
                name="editionDefinitionRules"
                value={rules}
                helpText={translate('EditionMatchRulesHelpText')}
                onChange={onRulesChange}
              />
            </FormGroup>
          </fieldset>
          {attributesValid ? null : (
            <Alert kind={kinds.DANGER}>
              {translate('EditionAttributesInvalid')}
            </Alert>
          )}
          {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
          {preview && previewCurrent ? (
            <>
              {preview.rejections.map((reason) => (
                <Alert key={reason} kind={kinds.DANGER}>
                  {reason}
                </Alert>
              ))}
              <p>
                {translate('EditionNormalizedAttributes')}:{' '}
                {Object.entries(preview.proposedEdition.attributes)
                  .map(([key, value]) => `${key}: ${value}`)
                  .join(', ')}
              </p>
              <Table columns={columns}>
                <TableBody>
                  {preview.samples.map((sample) => (
                    <TableRow key={sample.movieEditionId}>
                      <TableRowCell>{sample.name}</TableRowCell>
                      <TableRowCell>{sample.relativePath}</TableRowCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </>
          ) : null}
        </ModalBody>
        <ModalFooter>
          <Button isDisabled={!!busy} onPress={onCancel}>
            {translate('Cancel')}
          </Button>
          <SpinnerButton
            isSpinning={busy === 'preview'}
            isDisabled={!!busy || !name.trim() || !attributesValid}
            onPress={onPreview}
          >
            {translate('PreviewEditionFiles')}
          </SpinnerButton>
          <SpinnerButton
            kind={kinds.PRIMARY}
            isSpinning={busy === 'save'}
            isDisabled={!!busy || !previewCurrent || !preview?.editingEnabled}
            onPress={onSave}
          >
            {translate('Save')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

export default EditEditionDefinitionModal;
